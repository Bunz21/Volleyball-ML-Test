using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.MLAgents;
using Unity.VisualScripting;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using static UnityEngine.InputSystem.LowLevel.InputStateHistory;

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
    [SerializeField] private float touchCooldown = 0.75f;   // s
    [SerializeField] private int maxStepsBeforeDrop = 150;  // Steps before drop, ~3 sec if FixedUpdate is 0.02

    //– Scene references ------------------------------------------------
    [SerializeField] private GameObject ball;
    [SerializeField] private Rigidbody ballRb;
    [SerializeField] private Collider ballCol;

    [SerializeField] public GameObject blueGoal;
    [SerializeField] public GameObject redGoal;
    [SerializeField] public GameObject net;

    //– ML-Agents -------------------------------------------------------
    public int MaxEnvironmentSteps = 5000;

    /********************************************************
     *  RUNTIME-ONLY STATE  (not serialized)
     ********************************************************/

    //– Cached components ----------------------------------------------
    private Renderer blueGoalRenderer;
    private Renderer redGoalRenderer;
    private VolleyballSettings volleyballSettings;

    //- Materials ------------------------------------------------------
    public Material blueGoalMaterial;
    public Material redGoalMaterial;
    public Material defaultMaterial;

    //– Collections -----------------------------------------------------
    public List<VolleyballAgent> AgentsList = new();   // all agents
    public List<VolleyballAgent> BlueAgents =>
        AgentsList.FindAll(a => a.teamId == Team.Blue);
    public List<VolleyballAgent> RedAgents => 
        AgentsList.FindAll(a => a.teamId == Team.Red);

    private List<Renderer> RenderersList = new();  // both floor halves
    private readonly Dictionary<VolleyballAgent, float> lastTouchTime = new();

    //– Touch / rally tracking -----------------------------------------
    public int touchesBlue;
    public int touchesRed;
    public Team lastHitterTeam = Team.Default;
    public Team lastHitter = Team.Default;
    public VolleyballAgent lastHitterAgent = null;
    private bool ballPassedOverNet = false;
    private bool serveTouched = false;
    public bool lastHitWasSpike = false;

    //– Role & landing helpers -----------------------------------------
    private readonly Dictionary<VolleyballAgent, Role> currentRole = new();
    private Vector3 predictedLanding;      // court-local

    private const float roleTieEpsilon = 0.25f; // m² in X-Z

    //– Serve & reset logic --------------------------------------------
    private Team nextServer = Team.Default; // Default = “random first serve”
    private bool isBallFrozen = false;
    private int ballSpawnSide = -1;           // -1 blue court | 1 red court
    private int resetTimer;
    private bool isResettingRally = false;

    /********************************************************
     *  HELPERS
     ********************************************************/

    // Handy alias so calls read nicely (OpponentOf(team))
    private static Team OpponentOf(Team t) => (t == Team.Blue) ? Team.Red : Team.Blue;

    // Coroutine for floor flash
    private Coroutine floorFlashCo;

    // ------------------------------------------------------------
    // Simple one-line logger.  Toggle VERBOSE to silence everything
    // ------------------------------------------------------------
    #if UNITY_EDITOR
    const bool VERBOSE = false;
    #else
    const bool VERBOSE = false;
    #endif

    public void D(string msg)
    {
        if (VERBOSE) Debug.Log($"[EC] {Time.time:F2}s  {msg}");
    }

    private void CacheAgents()
    {
        if (AgentsList.Count != 0) return;             // already populated

        // true  -> include *inactive* children if you want them too
        var localAgents = GetComponentsInChildren<VolleyballAgent>(true);

        AgentsList.AddRange(localAgents);
        D($"CacheAgents – found {localAgents.Length} local agents");
    }

    void Awake()
    {
        CacheAgents();

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

        UpdateRoles();
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
            ResetScene();
        }

        // Only increment if ball is not frozen and serve has been touched
        if (isBallFrozen && !serveTouched)
        {
            if (resetTimer >= maxStepsBeforeDrop)
            {
                DropBall();
            }
        }
    }

    private void DropBall()
    {
        D(">>> DropBall CALLED! Unfreezing and enabling gravity.");
        isBallFrozen = false;
        if (ballCol != null) ballCol.isTrigger = false;
        if (ballRb != null)
        {
            ballRb.isKinematic = false;
            ballRb.useGravity = true;
            ballRb.linearVelocity = new Vector3(0f, -5f, 0f);   // Drop fast. Tune as needed.
            ballRb.angularVelocity = Vector3.zero;
        }
        Physics.SyncTransforms();
    }

    private Vector3 GetSpawnPosition(Team team, int slot, int totalAgents)
    {
        float courtWidth = 6f; // adjust as needed
        float x = -courtWidth / 2f + (courtWidth / (totalAgents - 1)) * slot;
        float y = 0.5f;
        float z = (team == Team.Blue) ? -7f : 7f;
        return new Vector3(x, y, z);
    }

    private Quaternion GetSpawnRotation(Team team)
    {
        // Blue faces +Z (0°), Red faces -Z (180°)
        float yaw = (team == Team.Blue) ? 0f : 180f;
        return Quaternion.Euler(0f, yaw, 0f);
    }

    public Vector3 PredictLanding(Rigidbody rb)
    {
        float vy = rb.linearVelocity.y, y0 = rb.position.y;
        float t = (vy + Mathf.Sqrt(vy * vy + 2 * Physics.gravity.y * -y0)) / -Physics.gravity.y;
        return transform.InverseTransformPoint(
            rb.position + rb.linearVelocity * t + 0.5f * Physics.gravity * t * t); // local
    }

    void UpdateRoles()
    {
        AssignRoles(Team.Blue);
        AssignRoles(Team.Red);
    }

    void AssignRoles(Team team)
    {
        // Get all agents on the specified team, sorted by their order in AgentsList
        var agents = AgentsList.FindAll(a => a.teamId == team);

        // Role order: 1st=Hitter, 2nd=Setter, 3rd=Passer, 4th=Hitter, 5th/6th=Passer, others=Generic
        for (int i = 0; i < agents.Count; i++)
        {
            switch (i)
            {
                case 0:
                    agents[i].role = Role.Hitter;
                    break;
                case 1:
                    agents[i].role = Role.Setter;
                    break;
                case 2:
                    agents[i].role = Role.Passer;
                    break;
                case 3:
                    agents[i].role = Role.Hitter;
                    break;
                case 4:
                case 5:
                    agents[i].role = Role.Passer;
                    break;
                default:
                    agents[i].role = Role.Generic;
                    break;
            }
        }
    }

    void RoleSpecificRewarding (VolleyballAgent agent, Team agentTeam, int numTouches, TouchType touchType)
    {
        switch (agent.role)
        {
            case Role.Hitter:
                RewardHitter(agent, agentTeam, numTouches, touchType);
                break;
            case Role.Setter:
                RewardSetter(agent, agentTeam, numTouches, touchType);
                break;
            case Role.Passer:
                RewardPasser(agent, agentTeam, numTouches, touchType);
                break;
            case Role.Generic:
                RewardPasser(agent, agentTeam, numTouches, touchType);
                break;
        }
    }

    void RewardHitter (VolleyballAgent agent, Team agentTeam, int numTouches, TouchType touchType)
    {
        switch (touchType)
        {
            case TouchType.Spike:
                switch (numTouches)
                {
                    case 1:
                        agent.AddReward(0.0001f);
                        break;
                    case 2:
                        agent.AddReward(0.001f);
                        break;
                    case 3:
                        agent.AddReward(0.02f);
                        break;
                }
                break;
            case TouchType.Set:
                switch (numTouches)
                {
                    case 1:
                        agent.AddReward(0.001f);
                        break;
                    case 2:
                        agent.AddReward(-0.01f);
                        break;
                    case 3:
                        agent.AddReward(0.001f);
                        break;
                }
                break;
            case TouchType.Bump:
                switch (numTouches)
                {
                    case 1:
                        agent.AddReward(0.002f);
                        break;
                    case 2:
                        agent.AddReward(-0.01f);
                        break;
                    case 3:
                        agent.AddReward(0.001f);
                        break;
                }
                break;
        }
    }

    void RewardSetter(VolleyballAgent agent, Team agentTeam, int numTouches, TouchType touchType)
    {
        switch (touchType)
        {
            case TouchType.Spike:
                switch (numTouches)
                {
                    case 1:
                        agent.AddReward(-0.01f);
                        break;
                    case 2:
                        agent.AddReward(0.0005f);
                        break;
                    case 3:
                        agent.AddReward(0.001f);
                        break;
                }
                break;
            case TouchType.Set:
                switch (numTouches)
                {
                    case 1:
                        agent.AddReward(0.0005f);
                        break;
                    case 2:
                        agent.AddReward(0.02f);
                        break;
                    case 3:
                        agent.AddReward(0.001f);
                        break;
                }
                break;
            case TouchType.Bump:
                switch (numTouches)
                {
                    case 1:
                        agent.AddReward(0.003f);
                        break;
                    case 2:
                        agent.AddReward(0.01f);
                        break;
                    case 3:
                        agent.AddReward(0.001f);
                        break;
                }
                break;
        }
    }

    void RewardPasser(VolleyballAgent agent, Team agentTeam, int numTouches, TouchType touchType)
    {
        switch (touchType)
        {
            case TouchType.Spike:
                switch (numTouches)
                {
                    case 1:
                        agent.AddReward(-0.01f);
                        break;
                    case 2:
                        agent.AddReward(0.0005f);
                        break;
                    case 3:
                        agent.AddReward(0.002f);
                        break;
                }
                break;
            case TouchType.Set:
                switch (numTouches)
                {
                    case 1:
                        agent.AddReward(0.005f);
                        break;
                    case 2:
                        agent.AddReward(0.005f);
                        break;
                    case 3:
                        agent.AddReward(0.001f);
                        break;
                }
                break;
            case TouchType.Bump:
                switch (numTouches)
                {
                    case 1:
                        agent.AddReward(0.02f);
                        break;
                    case 2:
                        agent.AddReward(0.005f);
                        break;
                    case 3:
                        agent.AddReward(0.005f);
                        break;
                }
                break;
        }
    }

    // -----------------------------------------------------------------------------
    //  ResetScene – full rally reset (agents, touches, ball)
    // -----------------------------------------------------------------------------
    public void ResetScene()
    {
        D("== ResetScene ==");
        resetTimer = 0;
        ballPassedOverNet = false;
        serveTouched = false;

        touchesBlue = 0;
        touchesRed = 0;
        lastHitter = Team.Default;
        lastHitterAgent = null;
        lastTouchTime.Clear();
        lastHitWasSpike = false;

        /* ---------- 1. teleport / zero-out every agent ------------------------- */
        int blueSlot = 0, redSlot = 0;

        foreach (var ag in AgentsList)
        {
            Team team = ag.teamId;
            int agentCountPerTeam = AgentsList.Count(a => a.teamId == team);
            int slot = (team == Team.Blue) ? blueSlot++ : redSlot++;
            slot = Mathf.Clamp(slot, 0, agentCountPerTeam - 1);

            Vector3 localPos = GetSpawnPosition(team, slot, agentCountPerTeam);
            Quaternion localRot = GetSpawnRotation(team);

            Vector3 worldPos = transform.TransformPoint(localPos);
            Quaternion worldRot = transform.rotation * localRot;

            ag.transform.SetPositionAndRotation(worldPos, worldRot);

            // clear rigidbody
            if (ag.TryGetComponent(out Rigidbody rb))
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            ag.role = Role.Generic; // reset role
        }

        /* ---------- 2. reset the ball ---------- */
        ResetBall();

        /* ---------- 3. log handy state summary --------------------------------- */
        D($"Scene reset   ballSide={(ballSpawnSide == -1 ? "Blue" : "Red")}");
    }

    // -----------------------------------------------------------------------------
    //  ResetBall – decides side, teleports, clears motion, re-freezes if wanted
    // -----------------------------------------------------------------------------
    private void ResetBall()
    {
        /* 1) Decide which court serves */
        if (nextServer == Team.Default)
        {
            // first rally – random
            ballSpawnSide = (Random.Range(0, 2) == 0) ? -1 : 1;
            nextServer = (ballSpawnSide == -1) ? Team.Blue : Team.Red;
        }
        else
        {
            ballSpawnSide = (nextServer == Team.Blue) ? 1 : -1; //SWAPPED LOSING TEAM SERVES
        }

        float xLocal = Random.Range(-2f, 2f); // Random X between -2 and +2
        float zLocal = (ballSpawnSide == -1) ? -4f : 4f;   // -Z blue, +Z red
        Vector3 localBallPos = new Vector3(xLocal, 2.75f, zLocal);

        // --- convert to world space ------------------------------
        ball.transform.position = transform.TransformPoint(localBallPos);
        ball.transform.rotation = transform.rotation;          // face same way

        /* 2) clear motion … (unchanged) */
        ballRb.isKinematic = false;
        ballRb.linearVelocity = Vector3.zero;
        ballRb.angularVelocity = Vector3.zero;

        /* 3) freeze or drop live */
        bool freezeOnServe = true;            // expose if you wish
        isBallFrozen = freezeOnServe;

        ballRb.useGravity = false;
        ballRb.isKinematic = true;
        if (ballCol != null) ballCol.isTrigger = true;

        Physics.SyncTransforms();

        D($"ResetBall  side={(ballSpawnSide == -1 ? "Blue" : "Red")}  server={nextServer}");
    }

    // ============================================================================
    //  SCORING HELPERS
    // ============================================================================

    /*------------------------------------------------------------
     * EndRally  – finalises a rally and starts the next one.
     *   winner : Team.Blue  or  Team.Red
     *   bonus  : extra shaping reward (0 by default)
     *-----------------------------------------------------------*/
    private void EndRally(Team winner)
    {
        if (isResettingRally)
        {
            //Debug.Log("[DEBUG] EndRally skipped, already resetting!");
            return;
        }
        isResettingRally = true;
        //Debug.Log("[DEBUG] EndRally STARTED");

        // 1) primary rewards
        float baseReward = 1f;
        AddTeamReward(winner, baseReward);
        AddTeamReward(OpponentOf(winner), -baseReward);

        // 2) next rally serves from the winning side
        FlashFloor(winner);
        nextServer = winner;

        // 3) finish episodes so stats roll up
        foreach (VolleyballAgent a in AgentsList)
        {
            a.EndEpisode();
        }

        // 4) hard reset
        D($"EndRally  winner={winner}");
        ResetScene();
        StartCoroutine(ResetRallyCooldown());
    }

    private IEnumerator ResetRallyCooldown()
    {
        yield return new WaitForSeconds(0.5f); // adjust as needed
        isResettingRally = false;
        //Debug.Log("[DEBUG] EndRally ready for next rally.");
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
        EndRally(scorer);
    }

    /*------------------------------------------------------------
     * ResolveEvent – minimal router for in-game triggers
     *   Call this from VolleyballController.OnTriggerEnter
     *-----------------------------------------------------------*/
    public void ResolveEvent(Event ev)
    {
        switch (ev)
        {
            case Event.HitRedGoal:
                {
                    if (lastHitterTeam == Team.Blue)
                    {
                        // Legal attack
                        AwardRegularPoint(Team.Blue);
                    }
                    else
                    {
                        AwardFaultAgainst(Team.Red);
                    }
                    break;
                }

            case Event.HitBlueGoal:
                {
                    if (lastHitterTeam == Team.Red)
                    {
                        AwardRegularPoint(Team.Red);
                    }
                    else
                    {
                        AwardFaultAgainst(Team.Blue);
                    }
                    break;
                }

            case Event.HitOutOfBounds:
                {
                    // Default to nextServer if lastHitter is not set
                    Team faultTeam = lastHitter == Team.Default ? nextServer : lastHitter;
                    AwardFaultAgainst(faultTeam);
                    break;
                }

            case Event.PassOverNet:
                {
                    // 1) Make sure the ball is now on the *opponent* side
                    bool ballNowOnRedSide = ball.transform.position.z > 0f;
                    bool ballNowOnBlueSide = ball.transform.position.z < 0f;

                    Team hitter = lastHitterTeam; // who struck last
                    if ((hitter == Team.Blue && !ballNowOnRedSide) ||
                        (hitter == Team.Red && !ballNowOnBlueSide))
                    {
                        // Crossed the trigger but bounced back – ignore
                        break;
                    }

                    // 2) Only mark the FIRST crossing each rally
                    if (!ballPassedOverNet)
                    {
                        ballPassedOverNet = true;

                        if (lastHitterAgent != null)
                        {
                            int touches = (lastHitterAgent.teamId == Team.Blue) ? touchesBlue : touchesRed;
                            AddTeamReward(lastHitterAgent.teamId, 0.005f * touches);
                        }

                        D("PassOverNet – flag set true");
                    }
                    break;
                }
        }
    }

    // ============================================================================
    //  REGISTER-TOUCH  – call from agent collider or VolleyballController
    // ============================================================================
    public void RegisterTouch(VolleyballAgent agent, TouchType touchType)
    {
        if (agent == null) return;

        ballPassedOverNet = false;

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

        // --- SPIKE-ON-SPIKE FAULT ---
        if (lastHitWasSpike && touchType == TouchType.Spike)
        {
            D($"Spike-on-spike by {agent.teamId}: rally ended");
            AwardFaultAgainst(agent.teamId);
            return;
        }

        lastHitWasSpike = touchType == TouchType.Spike;

        /*------------------------------------------------------------------
         * 3)  Legal touch – bookkeeping
         *-----------------------------------------------------------------*/
        UpdateLastHitter(agent);                     // sets lastHitter / Agent / Team

        // reset the *other* side’s counter because possession changed
        if (agent.teamId == Team.Blue)
            touchesRed = 0;
        else
            touchesBlue = 0;

        if (agent.teamId == Team.Blue) touchesBlue++;
        else touchesRed++;

        int touchesSoFar = (agent.teamId == Team.Blue) ? touchesBlue : touchesRed;
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

        RoleSpecificRewarding(agent, agent.teamId, touchesSoFar, touchType);
    }

    /// <summary>
    /// Activates the ball after a frozen serve on first touch
    /// </summary>
    /// <param name="toucher">Agent that touched the ball</param>
    public void ActivateBallFromServe(VolleyballAgent toucher)
    {
        if (!isBallFrozen) return;          // already active – nothing to do

        isBallFrozen = false;
        serveTouched = true;
        D($"Un-freeze on first touch by {toucher.teamId}");
        
        /* --- restore normal collider / physics -------------------------------- */
        if (ballCol != null) ballCol.isTrigger = false;

        ballRb.isKinematic = false;
        ballRb.useGravity = true;
        ballRb.linearVelocity = Vector3.zero;
        ballRb.angularVelocity = Vector3.zero;

        /* --- tiny incentive for the server to start the rally ----------------- */
        if (toucher != null) AddTeamReward(toucher.teamId, 0.005f);

        Physics.SyncTransforms();           // make PhysX pick up the changes NOW
    }

    /// <summary>
    /// Updates the last hitter info
    /// </summary>
    /// <param name="agent">Agent that touched the ball</param>
    void UpdateLastHitter(VolleyballAgent agent)
    {
        if (agent == null) return;

        lastHitterAgent = agent;
        lastHitterTeam = agent.teamId;
        lastHitter = agent.teamId;

        D($"UpdateLastHitter -> {lastHitter}");
    }

    /// <summary>
    /// Awards all agents on a team with the specified reward
    /// </summary>
    /// <param name="team">Rewarded team</param>
    /// /// <param name="reward">Reward value</param>
    public void AddTeamReward(Team team, float reward)
    {
        foreach (var agent in AgentsList.Where(a => a.teamId == team))
        {
            agent.AddReward(reward);
        }
    }


    /// <summary>
    /// Temporarily tints both ground halves with the winner’s colour.
    /// </summary>
    /// <param name="scoringTeam">Team.Blue or Team.Red</param>
    /// <param name="seconds">How long the flash should last</param>
    private void FlashFloor(Team scoringTeam, float seconds = 0.5f)
    {
        D($"FlashFloor called – team {scoringTeam}");
        if (RenderersList.Count == 0)
        {
            D("FlashFloor – no renderers registered! Did you forget to add them?");
            return;
        }

        // choose the material that matches the team that just scored
        Material mat = (scoringTeam == Team.Blue)
            ? volleyballSettings.blueGoalMaterial     // customise in your VolleyballSettings asset
            : volleyballSettings.redGoalMaterial;

        // stop an earlier flash so colours don’t overlap
        if (floorFlashCo != null)
            StopCoroutine(floorFlashCo);

        floorFlashCo = StartCoroutine(FloorFlashRoutine(mat, seconds));
    }
    /// <summary>
    /// Coroutine that handles the floor flash timing
    /// </summary>
    /// <param name="flashMat">Material to be used for flash</param>
    /// <param name="duration">How long the flash should last</param>

    private IEnumerator FloorFlashRoutine(Material flashMat, float duration)
    {
        // 1) apply tint
        foreach (Renderer r in RenderersList)
            r.material = flashMat;

        // 2) hold
        yield return new WaitForSeconds(duration);

        // 3) restore default
        foreach (Renderer r in RenderersList)
            r.material = volleyballSettings.defaultMaterial;

        floorFlashCo = null;      // allow a fresh flash next rally
    }
}
