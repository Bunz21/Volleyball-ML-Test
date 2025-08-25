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
    [SerializeField] private float velocityRewardForwardWeight = 0.03f;
    [SerializeField] private float velocityRewardDownWeight = 0.04f;
    [SerializeField] private float velocityRewardMax = 0.40f;
    [SerializeField] private float touchCooldown = 0.20f;   // s
    //[SerializeField] private float assistRewardSetter = 0.5f;  // earlier touch
    //[SerializeField] private float assistRewardSpiker = 0.5f;  // current touch
    [SerializeField] private int maxStepsBeforeDrop = 150;  // Steps before drop, ~3 sec if FixedUpdate is 0.02

    //– Scene references ------------------------------------------------
    [SerializeField] private GameObject ball;
    [SerializeField] private Rigidbody ballRb;
    [SerializeField] private Collider ballCol;

    [SerializeField] public GameObject blueGoal;
    [SerializeField] public GameObject redGoal;
    [SerializeField] public GameObject net;

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

    //- Materials ------------------------------------------------------
    public Material blueGoalMaterial;
    public Material redGoalMaterial;
    public Material defaultMaterial;

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
    private bool serveTouched = false;

    //– Role & landing helpers -----------------------------------------
    private readonly Dictionary<VolleyballAgent, Role> currentRole = new();
    private Vector3 predictedLanding;      // court-local

    private const float roleTieEpsilon = 0.25f; // m² in X-Z

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
            //if (!serveTouched)        // no one ever hit the ball
            //{
            //    const float idlePenalty = -0.4f;   // tune magnitude
            //    blueAgent.AddReward(idlePenalty);
            //    redAgent.AddReward(idlePenalty);
            //}

            D($"TIMEOUT – {MaxEnvironmentSteps} steps reached");
            blueAgent.EpisodeInterrupted();
            redAgent.EpisodeInterrupted();
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

    private Vector3 GetSpawnPosition(Team team, int slot)
    {
        // slot: 0 = left (-2), 1 = right (+2)
        float x = 0;
        float y = 0.5f;
        float z;
        if (team == Team.Blue)
        {
            z = (slot == 0) ? -3f : -7f;
        }
        else if (team == Team.Red)
        {
            z = (slot == 0) ? 3f : 7f;
        }
        else z = 0f;
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
        // work per side
        AssignRoles(Team.Blue);
        AssignRoles(Team.Red);
    }

    void AssignRoles(Team team)
    {
        // 1. Grab both agents for the team
        var agents = AgentsList.FindAll(a => a.teamId == team);
        if (agents.Count != 2) return;

        // 2. Compute predicted landing
        Vector2 landingXZ = new Vector2(predictedLanding.x, predictedLanding.z);

        // 3. Check if the ball is landing on this team's side
        bool ballOnOurSide = (team == Team.Blue)
            ? predictedLanding.z < 0f   // blue court is z < 0
            : predictedLanding.z > 0f;  // red court is z > 0

        if (!ballOnOurSide)
        {
            agents[0].role = Role.Generic;
            agents[1].role = Role.Generic;
            return;
        }

        // 4. Calculate distances in XZ plane
        float d0 = (new Vector2(agents[0].transform.localPosition.x, agents[0].transform.localPosition.z) - landingXZ).sqrMagnitude;
        float d1 = (new Vector2(agents[1].transform.localPosition.x, agents[1].transform.localPosition.z) - landingXZ).sqrMagnitude;

        // 5. Tie-break if agents are too close
        if (Mathf.Abs(d0 - d1) < roleTieEpsilon)
        {
            if (Random.value < 0.5f)
            {
                d0 = 0f; d1 = 1f;
            }
            else
            {
                d0 = 1f; d1 = 0f;
            }
        }

        // 6. Closest = Hitter, Farther = Passer (or vice versa as you want)
        if (d0 < d1)
        {
            agents[0].role = Role.Hitter;
            agents[1].role = Role.Passer;
        }
        else
        {
            agents[0].role = Role.Passer;
            agents[1].role = Role.Hitter;
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

        /* ---------- 1. teleport / zero-out every agent ------------------------- */
        int blueSlot = 0, redSlot = 0;

        foreach (var ag in AgentsList)
        {
            Team team = ag.teamId;
            int slot = (team == Team.Blue) ? blueSlot++ : redSlot++;
            slot = Mathf.Clamp(slot, 0, 1);            // only 0 or 1

            // 1) local offsets inside the court prefab
            Vector3 localPos = GetSpawnPosition(team, slot);
            Quaternion localRot = GetSpawnRotation(team);

            // 2) convert to *scene* space using THIS prefab’s transform
            Vector3 worldPos = transform.TransformPoint(localPos);
            Quaternion worldRot = transform.rotation * localRot;

            ag.transform.SetPositionAndRotation(worldPos, worldRot);

            // clear rigidbody
            if (ag.TryGetComponent(out Rigidbody rb))
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            if (slot == 0)
            {
                ag.role = Role.Hitter;
            }
            else if (slot == 1)
            {
                ag.role = Role.Passer;
            }
            else
            {
                ag.role = Role.Generic;
            }
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
            ballSpawnSide = (nextServer == Team.Blue) ? -1 : 1;
        }

        float zLocal = (ballSpawnSide == -1) ? -4f : 4f;   // -Z blue, +Z red
        Vector3 localBallPos = new Vector3(0f, 2.5f, zLocal);

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

        // 2) next rally serves from the winning side
        FlashFloor(winner);
        nextServer = winner;

        // 3) finish episodes so stats roll up
        blueAgent.EndEpisode();
        redAgent.EndEpisode();

        // 4) hard reset
        D($"EndRally  winner={winner}  bonus={bonus:F2}");
        ResetScene();
    }

    /*------------------------------------------------------------
     * AwardFaultAgainst – generic fault handler
     *-----------------------------------------------------------*/
    private void AwardFaultAgainst(Team faultyTeam)
    {
        Team winner = OpponentOf(faultyTeam);

        float bonus = 0f;
        if (ballRb != null)
        {
            Vector3 v = ballRb.linearVelocity;
            float forward = (winner == Team.Blue) ? Mathf.Max(0f, v.z)
                                                  : Mathf.Max(0f, -v.z);
            float down = Mathf.Max(0f, -v.y);
            bonus = Mathf.Min(
                               velocityRewardForwardWeight * forward +
                               velocityRewardDownWeight * down,
                               velocityRewardMax);
        }

        D($"FAULT against {faultyTeam}  -> point for {winner}");
        EndRally(winner);
    }

    /*------------------------------------------------------------
     * AwardRegularPoint – called when the ball legally lands in-bounds
     *-----------------------------------------------------------*/
    private void AwardRegularPoint(Team scorer)
    {
        // small velocity-shaping bonus
        //float bonus = 0f;
        //if (ballRb != null)
        //{
        //    Vector3 v = ballRb.linearVelocity;

        //    // +Z faces Red side, -Z faces Blue side
        //    float forward = (scorer == Team.Blue) ? Mathf.Max(0f, v.z)
        //                                          : Mathf.Max(0f, -v.z);
        //    float down = Mathf.Max(0f, -v.y);

        //    bonus = velocityRewardForwardWeight * forward +
        //            velocityRewardDownWeight * down;

        //    bonus = Mathf.Min(bonus, velocityRewardMax);
        //}

        //D($"Regular point for {scorer}  bonus={bonus:F2}");
        //EndRally(scorer, bonus);

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
                        // Self-goal
                        //if (lastHitterAgent != null && ballPassedOverNet)
                        //{
                        //    lastHitterAgent.AddReward(-0.5f);
                        //}
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
                        //if (lastHitterAgent != null && ballPassedOverNet)
                        //{
                        //    lastHitterAgent.AddReward(-0.5f);
                        //}
                        AwardFaultAgainst(Team.Blue);
                    }
                    break;
                }

            case Event.HitOutOfBounds:
                {
                    //if (lastHitterAgent != null)
                    //{
                    //    lastHitterAgent.AddReward(-0.05f);
                    //}
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
                        UpdateRoles();

                        if (lastHitterAgent != null)
                        {
                            lastHitterAgent.AddReward(0.3f);
                        }

                        // Uncomment and adjust if you want to further shape the reward for spikes/sets
                        /*
                        int touchesSoFar = (lastHitterAgent.teamId == Team.Blue) ? touchesBlue : touchesRed;
                        if (touchesSoFar == 3 && lastHitterAgent.role == Role.Hitter)
                        {
                            lastHitterAgent.AddReward(assistRewardSpiker);
                        }
                        if (touchesSoFar < 3)
                        {
                            lastHitterAgent.AddReward(-0.2f);
                        }
                        */
                        D("PassOverNet – flag set true");
                    }
                    break;
                }
        }
    }

    // ============================================================================
    //  REGISTER-TOUCH  – call from agent collider or VolleyballController
    // ============================================================================
    public void RegisterTouch(VolleyballAgent agent)
    {
        if (agent == null) return;                   // safety
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

        /*------------------------------------------------------------------
         * 2.5)  ASSIST / SET-SPIKE bonus
         *       (teammate touched last, ball hasn’t crossed yet)
         *-----------------------------------------------------------------*/
        // Which counter to read?

        //if (lastHitterAgent != null &&
        //    lastHitterAgent.teamId == agent.teamId &&      // same side
        //    lastHitterAgent != agent &&                    // different player
        //    touchesSoFar == 1 && !ballPassedOverNet)       // still same rally phase
        //{
        //    lastHitterAgent.AddReward(assistRewardSetter); // the setter
        //}

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

        //Vector3 forward = (agent.teamId == Team.Blue) ? Vector3.forward : Vector3.back;
        //Vector3 ballVel = ballRb.linearVelocity;
        //if (ballVel.magnitude > 0.1f) // Ignore near-zero (taps)
        //{
        //    float dirScore = Vector3.Dot(ballVel.normalized, forward); // -1 to +1
        //    if (dirScore < -0.5f)
        //        lastHitterAgent.AddReward(dirScore * 0.1f); // Strong penalty for wild back hits
        //    else if (dirScore > 0.1f)
        //        lastHitterAgent.AddReward(dirScore * 0.07f); // Reward for forward or slightly forward
        //                                                     // Do nothing for -0.5f < dirScore < 0.1f (sideways or gentle back)
        //}

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
        serveTouched = true;
        D($"Un-freeze on first touch by {toucher.teamId}");

        /* --- restore normal collider / physics -------------------------------- */
        if (ballCol != null) ballCol.isTrigger = false;

        ballRb.isKinematic = false;
        ballRb.useGravity = true;
        ballRb.linearVelocity = Vector3.zero;
        ballRb.angularVelocity = Vector3.zero;

        /* --- tiny incentive for the server to start the rally ----------------- */
        if (toucher != null) toucher.AddReward(0.005f);

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
