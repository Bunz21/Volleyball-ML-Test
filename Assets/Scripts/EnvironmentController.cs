using System.Collections;
using System.Collections.Generic;
using Unity.MLAgents;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

public enum Team
{
    Blue = 0,
    Red = 1,
    Default = 2
}

public enum Event
{
    HitRedGoal = 0,
    HitBlueGoal = 1,
    HitOutOfBounds = 2,
    PassOverNet = 3,
    AgentTouch = 4
}

public class EnvironmentController : MonoBehaviour
{
    /********************************************************
 *  CONFIGURATION  (serialized in the Inspector)
 ********************************************************/

    //– Gameplay tuning -------------------------------------------------
    [SerializeField] private float velocityRewardForwardWeight = 0.02f;
    [SerializeField] private float velocityRewardDownWeight = 0.03f;
    [SerializeField] private float velocityRewardMax = 0.40f;
    [SerializeField] private float touchCooldown = 0.10f;   // s

    //– Scene references ------------------------------------------------
    [SerializeField] private GameObject ball;
    [SerializeField] private Rigidbody ballRb;
    [SerializeField] private Collider ballCol;

    [SerializeField] private GameObject blueGoal;
    [SerializeField] private GameObject redGoal;

    [SerializeField] private VolleyballAgent blueAgent;   // “primary” blue
    [SerializeField] private VolleyballAgent redAgent;    // “primary” red

    //– ML-Agents -------------------------------------------------------
    public int MaxEnvironmentSteps = 5000;

    /********************************************************
     *  RUNTIME-ONLY STATE  (not serialized)
     ********************************************************/

    //– Cached components ----------------------------------------------
    private Rigidbody blueAgentRb;
    private Rigidbody redAgentRb;
    private Renderer blueGoalRenderer;
    private Renderer redGoalRenderer;
    private VolleyballSettings volleyballSettings;

    //– Collections -----------------------------------------------------
    public List<VolleyballAgent> AgentsList = new();   // all 4 agents
    private List<Renderer> RenderersList = new();  // both floor halves
    private readonly Dictionary<VolleyballAgent, float> lastTouchTime = new();

    //– Touch / rally tracking -----------------------------------------
    public int touchesBlue;
    public int touchesRed;
    public Team lastHitterTeam = Team.Default;
    public Team lastHitter = Team.Default;
    public VolleyballAgent lastHitterAgent = null;
    private bool ballPassedOverNet = false;

    //– Serve & reset logic --------------------------------------------
    private Team nextServer = Team.Default; // Default = “random first serve”
    private bool isBallFrozen = false;
    private int ballSpawnSide = -1;           // -1 blue court | 1 red court
    private int resetTimer;

    /********************************************************
     *  HELPERS
     ********************************************************/

    // Handy alias so calls read nicely (OpponentOf(team))
    private static Team OpponentOf(Team t) => (t == Team.Blue) ? Team.Red : Team.Blue;

    // ------------------------------------------------------------
    // Simple one-line logger.  Toggle VERBOSE to silence everything
    // ------------------------------------------------------------
    #if UNITY_EDITOR
    const bool VERBOSE = true;
    #else
    const bool VERBOSE = false;
    #endif

    public void D(string msg)
    {
        if (VERBOSE) Debug.Log($"[EC] {Time.time:F2}s  {msg}");
    }


    void Awake()
    {
        if (ballRb == null && ball != null) ballRb = ball.GetComponent<Rigidbody>();
        if (ballCol == null && ball != null) ballCol = ball.GetComponent<Collider>();
        touchesBlue = 0;
        touchesRed = 0;
        D("Awake – grabbed ballRb/ballCol");
    }

    void Start()
    {
        D("Start – scene initialised, calling ResetScene");
        // Used to control agent & ball starting positions
        blueAgentRb = blueAgent.GetComponent<Rigidbody>();
        redAgentRb = redAgent.GetComponent<Rigidbody>();
        ballRb = ball.GetComponent<Rigidbody>();

        // Starting ball spawn side
        // -1 = spawn blue side, 1 = spawn red side
        var spawnSideList = new List<int> { -1, 1 };
        ballSpawnSide = spawnSideList[Random.Range(0, 2)];

        // Render ground to visualise which agent scored
        blueGoalRenderer = blueGoal.GetComponent<Renderer>();
        redGoalRenderer = redGoal.GetComponent<Renderer>();
        RenderersList.Add(blueGoalRenderer);
        RenderersList.Add(redGoalRenderer);

        volleyballSettings = FindFirstObjectByType<VolleyballSettings>();

        ResetScene();
    }

    // -----------------------------------------------------------------------------
    //  FixedUpdate – just the time-out watchdog
    // -----------------------------------------------------------------------------
    private void FixedUpdate()
    {
        resetTimer++;
        if (MaxEnvironmentSteps > 0 && resetTimer >= MaxEnvironmentSteps)
        {
            D($"TIMEOUT – {MaxEnvironmentSteps} steps reached");
            blueAgent.EpisodeInterrupted();
            redAgent.EpisodeInterrupted();
            ResetScene();
        }
    }

    private Vector3 GetSpawnPosition(Team team, int slot)
    {
        // slot: 0 = left (-2), 1 = right (+2)
        float x = (slot == 0) ? -2f : 2f;
        float y = 1f;
        float z = (team == Team.Blue) ? -7f : 7f;
        return new Vector3(x, y, z);
    }

    private Quaternion GetSpawnRotation(Team team)
    {
        // Blue faces +Z (0°), Red faces -Z (180°)
        float yaw = (team == Team.Blue) ? 0f : 180f;
        return Quaternion.Euler(0f, yaw, 0f);
    }

    // -----------------------------------------------------------------------------
    //  ResetScene – full rally reset (agents, touches, ball)
    // -----------------------------------------------------------------------------
    public void ResetScene()
    {
        D("== ResetScene ==");
        resetTimer = 0;
        ballPassedOverNet = false;

        touchesBlue = 0;
        touchesRed = 0;
        lastHitter = Team.Default;
        lastHitterAgent = null;
        lastTouchTime.Clear();

        /* ---------- 1. teleport / zero-out every agent ------------------------- */
        int blueSlot = 0, redSlot = 0;
        foreach (var ag in AgentsList)
        {
            Team team = ag.teamId;

            int slot = (team == Team.Blue) ? blueSlot++ : redSlot++;
            if (slot > 1) slot = 1;                     // only two slots per side

            ag.transform.SetPositionAndRotation(
                GetSpawnPosition(team, slot),
                GetSpawnRotation(team)
            );

            var rb = ag.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        /* ---------- 2. reset the ball ------------------------------------------ */
        ResetBall();

        /* ---------- 3. log handy state summary --------------------------------- */
        D($"Scene reset   ballSide={(ballSpawnSide == -1 ? "Blue" : "Red")}");
    }

    // -----------------------------------------------------------------------------
    //  ResetBall – decides side, teleports, clears motion, re-freezes if wanted
    // -----------------------------------------------------------------------------
    private void ResetBall()
    {
        /* 1) alternate spawn side so rallies always switch */
        ballSpawnSide *= -1;                 // first call will set to ±1 correctly
        float z = (ballSpawnSide == -1) ? -4f : 4f;   // blue court is -Z

        ball.transform.localPosition = new Vector3(0f, 2.5f, z);

        /* 2) clear physics state (has to be non-kinematic while we do it) */
        if (ballRb == null) ballRb = ball.GetComponent<Rigidbody>();

        ballRb.isKinematic = false;
        ballRb.linearVelocity = Vector3.zero;
        ballRb.angularVelocity = Vector3.zero;

        /* 3) optionally drop it frozen in mid-air – hook your own flag here */
        bool freezeOnServe = true;           // <-- expose as [SerializeField] later
        if (freezeOnServe)
        {
            ballRb.useGravity = false;
            ballRb.isKinematic = true;       // keep suspended until first touch
        }
        else
        {
            ballRb.useGravity = true;
        }

        Physics.SyncTransforms();

        D($"ResetBall  side={(ballSpawnSide == -1 ? "Blue" : "Red")}  freeze={freezeOnServe}");
    }

    // ============================================================================
    //  SCORING HELPERS
    // ============================================================================

    /*------------------------------------------------------------
     * EndRally  – finalises a rally and starts the next one.
     *   winner : Team.Blue  or  Team.Red
     *   bonus  : extra shaping reward (0 by default)
     *-----------------------------------------------------------*/
    private void EndRally(Team winner, float bonus = 0f)
    {
        // 1) primary rewards
        float baseReward = 1f;
        if (winner == Team.Blue)
        {
            blueAgent.AddReward(baseReward + bonus);
            redAgent.AddReward(-baseReward);
        }
        else
        {
            redAgent.AddReward(baseReward + bonus);
            blueAgent.AddReward(-baseReward);
        }

        // 2) UI feedback
        //FlashFloor(winner);

        // 3) next rally serves from the winning side
        nextServer = winner;

        // 4) finish episodes so stats roll up
        blueAgent.EndEpisode();
        redAgent.EndEpisode();

        // 5) hard reset
        D($"EndRally  winner={winner}  bonus={bonus:F2}");
        ResetScene();
    }

    /*------------------------------------------------------------
     * AwardFaultAgainst – generic fault handler
     *-----------------------------------------------------------*/
    private void AwardFaultAgainst(Team faultyTeam)
    {
        Team winner = OpponentOf(faultyTeam);
        D($"FAULT against {faultyTeam}  -> point for {winner}");
        EndRally(winner);
    }

    /*------------------------------------------------------------
     * AwardRegularPoint – called when the ball legally lands in-bounds
     *-----------------------------------------------------------*/
    private void AwardRegularPoint(Team scorer)
    {
        // small velocity-shaping bonus
        float bonus = 0f;
        if (ballRb != null)
        {
            Vector3 v = ballRb.linearVelocity;

            // +Z faces Red side, -Z faces Blue side
            float forward = (scorer == Team.Blue) ? Mathf.Max(0f, v.z)
                                                  : Mathf.Max(0f, -v.z);
            float down = Mathf.Max(0f, -v.y);

            bonus = velocityRewardForwardWeight * forward +
                    velocityRewardDownWeight * down;

            bonus = Mathf.Min(bonus, velocityRewardMax);
        }

        D($"Regular point for {scorer}  bonus={bonus:F2}");
        EndRally(scorer, bonus);
    }

    /*------------------------------------------------------------
     * ResolveEvent – minimal router for in-game triggers
     *   Call this from VolleyballController.OnTriggerEnter
     *-----------------------------------------------------------*/
    public void ResolveEvent(Event ev)
    {
        switch (ev)
        {
            case Event.HitRedGoal:    // ball touched RED floor
                if (ballPassedOverNet) AwardRegularPoint(Team.Blue);
                else AwardFaultAgainst(Team.Blue);  // hit own side
                break;

            case Event.HitBlueGoal:   // ball touched BLUE floor
                if (ballPassedOverNet) AwardRegularPoint(Team.Red);
                else AwardFaultAgainst(Team.Red);
                break;

            case Event.HitOutOfBounds:        // went outside court limits
                AwardFaultAgainst(lastHitter == Team.Default ? nextServer : lastHitter);
                break;

            case Event.PassOverNet:           // cleared the net – mark it
                ballPassedOverNet = true;
                D("PassOverNet – flag set true");
                break;
        }
    }

    // ============================================================================
    //  REGISTER-TOUCH  – call from agent collider or VolleyballController
    // ============================================================================
    public void RegisterTouch(VolleyballAgent agent)
    {
        if (agent == null) return;                   // safety

        /*------------------------------------------------------------------
         * 0)  Cool-down: ignore “micro-bounces” from the same collider
         *-----------------------------------------------------------------*/
        float now = Time.time;
        if (lastTouchTime.TryGetValue(agent, out float tPrev) &&
            (now - tPrev) < touchCooldown)
        {
            D($"RegisterTouch  [{agent.teamId}]  IGNORED (cool-down)");
            return;
        }
        lastTouchTime[agent] = now;

        /*------------------------------------------------------------------
         * 1)  First touch after a frozen serve? -> release the ball
         *-----------------------------------------------------------------*/
        ActivateBallFromServe(agent);                // does nothing if already free

        /*------------------------------------------------------------------
         * 2)  DOUBLE-TOUCH fault  (same agent twice in a row)
         *     Compare *before* we overwrite lastHitterAgent!
         *-----------------------------------------------------------------*/
        if (lastHitterAgent == agent)
        {
            D($"Double-touch fault by {agent.teamId}");
            AwardFaultAgainst(agent.teamId);         // winner decided inside
            return;                                  // rally ended
        }

        /*------------------------------------------------------------------
         * 3)  Legal touch – bookkeeping
         *-----------------------------------------------------------------*/
        UpdateLastHitter(agent);                     // sets lastHitter / Agent / Team

        if (agent.teamId == Team.Blue) touchesBlue++;
        else touchesRed++;

        D($"RegisterTouch by {agent.teamId}  TB/TR={touchesBlue}/{touchesRed}");

        /*------------------------------------------------------------------
         * 4)  FOUR-TOUCH fault on a side
         *-----------------------------------------------------------------*/
        if (touchesBlue > 3)
        {
            D("Four-touch fault on BLUE");
            AwardFaultAgainst(Team.Blue);
            return;
        }
        if (touchesRed > 3)
        {
            D("Four-touch fault on RED");
            AwardFaultAgainst(Team.Red);
            return;
        }

        /*------------------------------------------------------------------
         * 5)  Rally continues – do NOT touch ballPassedOverNet here
         *-----------------------------------------------------------------*/
    }

    // ============================================================================
    //  ACTIVATE-BALL-FROM-SERVE  – first legal contact of the rally
    // ============================================================================
    public void ActivateBallFromServe(VolleyballAgent toucher)
    {
        if (!isBallFrozen) return;          // already active – nothing to do

        isBallFrozen = false;
        D($"Un-freeze on first touch by {toucher.teamId}");

        /* --- restore normal collider / physics -------------------------------- */
        if (ballCol != null) ballCol.isTrigger = false;

        ballRb.isKinematic = false;
        ballRb.useGravity = true;
        ballRb.linearVelocity = Vector3.zero;
        ballRb.angularVelocity = Vector3.zero;

        /* --- tiny incentive for the server to start the rally ----------------- */
        if (toucher != null) toucher.AddReward(0.05f);

        Physics.SyncTransforms();           // make PhysX pick up the changes NOW
    }

    // ============================================================================
    //  UPDATE-LAST-HITTER  – call only after verifying the touch was legal
    // ============================================================================
    void UpdateLastHitter(VolleyballAgent agent)
    {
        if (agent == null) return;

        lastHitterAgent = agent;
        lastHitterTeam = agent.teamId;
        lastHitter = agent.teamId;

        D($"UpdateLastHitter -> {lastHitter}");
    }

}
