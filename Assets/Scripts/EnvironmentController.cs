using System.Collections;
using System.Collections.Generic;
using Unity.MLAgents;
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
    // ------------------------------------------------------------
    // Simple one-line logger.  Toggle VERBOSE to silence everything
    // ------------------------------------------------------------
    #if UNITY_EDITOR
    const bool VERBOSE = true;
    #else
    const bool VERBOSE = false;
    #endif

    void D(string msg)
    {
        if (VERBOSE) Debug.Log($"[EC] {Time.time:F2}s  {msg}");
    }


    int ballSpawnSide;

    // Add near top of EnvironmentController
    [SerializeField] float velocityRewardForwardWeight = 0.02f;  // per m/s toward opponent
    [SerializeField] float velocityRewardDownWeight = 0.03f;  // per m/s downward
    [SerializeField] float velocityRewardMax = 0.4f;   // cap

    // 1.  add a field for quick access
    [SerializeField] private Collider ballCol;

    VolleyballSettings volleyballSettings;

    // Serve-freeze config
    [SerializeField] bool freezeOnServe = true;
    //[SerializeField] float serveTouchUpImpulse = 2.0f;     // small pop so it doesn't die at feet
    //[SerializeField] float serveTouchFwdImpulse = 1.5f;    // gentle nudge in the hitter's forward
    bool isBallFrozen = false;

    public VolleyballAgent blueAgent;
    public VolleyballAgent redAgent;

    public List<VolleyballAgent> AgentsList = new List<VolleyballAgent>();
    List<Renderer> RenderersList = new List<Renderer>();
    private Team OpponentOf(Team t) => (t == Team.Blue) ? Team.Red : Team.Blue;
    private bool ballPassedOverNet = false;

    Rigidbody blueAgentRb;
    Rigidbody redAgentRb;

    public GameObject ball;
    [SerializeField] Rigidbody ballRb;

    public GameObject blueGoal;
    public GameObject redGoal;

    private Team nextServer = Team.Default;   // Default = “pick randomly once”

    Renderer blueGoalRenderer;

    Renderer redGoalRenderer;

    Team lastHitter;

    private int resetTimer;
    public int MaxEnvironmentSteps;

    // Touch tracking
    public int touchesBlue = 0;
    public int touchesRed = 0;
    public Team lastHitterTeam = Team.Default;              // or a Default/None if you have one
    public VolleyballAgent lastHitterAgent = null;

    // To prevent multi-count from the same physics frame / quick recontacts
    [SerializeField] float touchCooldown = 0.10f;        // seconds
    private Dictionary<VolleyballAgent, float> lastTouchTime = new Dictionary<VolleyballAgent, float>();

    // ------------------------------------------------------------------
    //  put this in EnvironmentController
    // ------------------------------------------------------------------
    private Coroutine floorFlashCo;         // keep a handle to the running job

    void FlashFloor(Team scoringTeam, float seconds = 0.5f)
    {
        // pick the material that matches the team that *won the point*
        Material mat = (scoringTeam == Team.Blue)
                       ? volleyballSettings.blueGoalMaterial
                       : volleyballSettings.redGoalMaterial;

        // stop any previous flash so colours never clash
        if (floorFlashCo != null) StopCoroutine(floorFlashCo);
        floorFlashCo = StartCoroutine(FloorFlashRoutine(mat, seconds));
        D("FlashFloor color " + scoringTeam);
    }

    IEnumerator FloorFlashRoutine(Material mat, float time)
    {
        D("Flashing color " + mat);
        foreach (var r in RenderersList)
            r.material = mat;

        yield return new WaitForSeconds(time);

        foreach (var r in RenderersList)
            r.material = volleyballSettings.defaultMaterial;

        floorFlashCo = null;              // allow new flashes again
    }

    void Awake()
    {
        if (ballRb == null && ball != null) ballRb = ball.GetComponent<Rigidbody>();
        if (ballCol == null && ball != null) ballCol = ball.GetComponent<Collider>();
        D("Awake – grabbed ballRb/ballCol");
    }

    void Start()
    {
        D("Start – scene initialised, calling ResetScene");
        // Used to control agent & ball starting positions
        blueAgentRb = blueAgent.GetComponent<Rigidbody>();
        redAgentRb = redAgent.GetComponent<Rigidbody>();

        // Render ground to visualise which agent scored
        blueGoalRenderer = blueGoal.GetComponent<Renderer>();
        redGoalRenderer = redGoal.GetComponent<Renderer>();
        RenderersList.Add(blueGoalRenderer);
        RenderersList.Add(redGoalRenderer);

        volleyballSettings = FindFirstObjectByType<VolleyballSettings>();

        ResetScene();
    }

    /// <summary>
    /// Tracks which agent last had control of the ball
    /// </summary>
    void UpdateLastHitter(VolleyballAgent agent)
    {
        lastHitterAgent = agent;
        lastHitterTeam = agent.teamId;
        lastHitter = agent.teamId;
        D($"UpdateLastHitter -> {lastHitter}");
    }

    // ---------------------------------------------------------------------------
    // Call this ONE line whenever the rally is over:
    //     EndRally(Team.Blue);   // Blue scored
    //     EndRally(Team.Red);    // Red scored
    // ---------------------------------------------------------------------------
    void EndRally(Team winner, float bonus = 0f)
    {
        Team loser = (winner == Team.Blue) ? Team.Red : Team.Blue;
        D($"EndRally – {winner} wins (bonus {bonus:0.00}), nextServer = {winner}");

        // 1. primary rewards (add any shaping bonus you computed earlier)
        if (winner == Team.Blue)
        {
            blueAgent.AddReward(1f + bonus);
            redAgent.AddReward(-1f);
        }
        else
        {
            redAgent.AddReward(1f + bonus);
            blueAgent.AddReward(-1f);
        }

        // 2. visual feedback
        FlashFloor(winner);

        // 3. the team that won will serve next
        nextServer = winner;

        // 4. finish the episodes so stats roll up
        blueAgent.EndEpisode();
        redAgent.EndEpisode();

        // 5. clear rally-state and drop a new ball on the server’s side
        ResetScene();
    }

    public void ActivateBallFromServe(VolleyballAgent toucher)
    {
        if (!isBallFrozen) return;

        isBallFrozen = false;
        ballCol.isTrigger = false;   // solid again – must come *before* physics step
        ballRb.isKinematic = false;
        ballRb.useGravity = true;
        ballRb.linearVelocity = Vector3.zero;
        ballRb.angularVelocity = Vector3.zero;

        if (toucher != null)
            toucher.AddReward(0.05f);

        // make sure PhysX sees the change immediately
        Physics.SyncTransforms();
    }


    public void RegisterTouch(VolleyballAgent agent)
    {
        if (agent == null) return;
        
        /* 0. cool-down --------------------------------------------------------- */
        float now = Time.time;
        if (lastTouchTime.TryGetValue(agent, out float tLast) &&
            (now - tLast) < touchCooldown)
            return;
        lastTouchTime[agent] = Time.frameCount;
        
        /* 1. first touch after serve?  ---------------------------------------- */
        ActivateBallFromServe(agent);   // harmless if already active

        /* 2. double-touch fault ----------------------------------------------- */
        if (lastHitterAgent == agent)
        {
            D("Double-touch fault");
            EndRally(OpponentOf(lastHitter));   // double-touch or 4-touch fault
            return;
        }

        /* 3. bookkeeping for a legal touch ------------------------------------ */
        UpdateLastHitter(agent);

        if (agent.teamId == Team.Blue) touchesBlue++;
        else touchesRed++;

        /* 4. four-touch fault -------------------------------------------------- */
        if (touchesBlue > 3 || touchesRed > 3)
        {
            D("Four-touch fault");
            EndRally(OpponentOf(lastHitter));   // double-touch or 4-touch fault
            return;
        }
        D($"RegisterTouch by {agent.teamId} | lastHitter={lastHitter}  TB/TR={touchesBlue}/{touchesRed}");

        /* 5. rally continues — *do not* touch ballPassedOverNet here ---------- */
    }

    /// <summary>
    /// Resolves scenarios when ball enters a trigger and assigns rewards.
    /// Example reward functions are shown below.
    /// To enable Self-Play: Set either Red or Blue Agent's Team ID to 1.
    /// </summary>
    public void ResolveEvent(Event triggerEvent)
    {
        switch (triggerEvent)
        {
            case Event.HitOutOfBounds:
                D($"OutOfBounds   lastHitter={lastHitter}");
                EndRally(OpponentOf(lastHitter == Team.Default ? nextServer : lastHitter));
                break;

            case Event.HitRedGoal:   // ball landed on the RED floor
                D($"HitRedGoal | crossed={ballPassedOverNet}  lastHitter={lastHitter}");
                {
                    if (ballPassedOverNet)
                        EndRally(Team.Blue);          // Blue scored cleanly
                    else
                        EndRally(OpponentOf(lastHitter == Team.Default ? nextServer : lastHitter));
                    break;
                }

            case Event.HitBlueGoal:  // ball landed on the BLUE floor
                D($"HitBlueGoal | crossed={ballPassedOverNet}  lastHitter={lastHitter}");
                {
                    if (ballPassedOverNet)
                        EndRally(Team.Red);           // Red scored cleanly
                    else
                        EndRally(OpponentOf(lastHitter == Team.Default ? nextServer : lastHitter));
                    break;
                }


            case Event.PassOverNet:
                D("PassOverNet – flag set TRUE");
                {
                    // Mark that a legal crossing occurred; optional small shaping reward to last hitter
                    ballPassedOverNet = true;

                    if (lastHitter == Team.Red)
                    {
                        switch (touchesRed)
                        {
                            case 1:
                            default:
                                redAgent.AddReward(0.1f);
                                break;
                            case 2:
                                redAgent.AddReward(0.2f);
                                break;
                            case 3:
                                redAgent.AddReward(0.3f);
                                break;
                        }
                    }
                    else if (lastHitter == Team.Blue)
                    {
                        switch (touchesBlue)
                        {
                            case 1:
                            default:
                                blueAgent.AddReward(0.1f);
                                break;
                            case 2:
                                blueAgent.AddReward(0.2f);
                                break;
                            case 3:
                                blueAgent.AddReward(0.3f);
                                break;
                        }
                    }
                    touchesBlue = 0;
                    touchesRed = 0;
                    lastHitterAgent = null;
                    break;
                }
        }
    }

    /// <summary>
    /// Called every step. Control max env steps.
    /// </summary>
    void FixedUpdate()
    {
        resetTimer += 1;
        if (resetTimer >= MaxEnvironmentSteps && MaxEnvironmentSteps > 0)
        {
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

    public void ResetScene()
    {
        D("ResetScene – spawning agents & resetting rally state");
        resetTimer = 0;
        lastHitter = Team.Default;
        ballPassedOverNet = false;
        touchesBlue = 0;
        touchesRed = 0;
        lastHitterAgent = null;
        lastHitterTeam = Team.Default;
        lastTouchTime.Clear();

        // deterministic slots per team
        int blueSlot = 0;
        int redSlot = 0;

        foreach (var agent in AgentsList)
        {
            var vAgent = agent.GetComponent<VolleyballAgent>(); // or your agent type
            Team team = vAgent != null ? vAgent.teamId : Team.Blue; // fallback if needed

            int slot = (team == Team.Blue) ? blueSlot++ : redSlot++;
            if (slot > 1) slot = 1; // clamp if more than 2 per side

            agent.transform.SetPositionAndRotation(
                GetSpawnPosition(team, slot),
                GetSpawnRotation(team)
            );

            var rb = agent.GetComponent<Rigidbody>();
            if (rb)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        ResetBall(); // center ball, or mini-serve next
    }

    /// <summary>
    /// Reset ball spawn conditions
    /// </summary>
    void ResetBall()
    {
        /* ------------------------------------------------
         * 1)  Decide which side serves this rally
         * ------------------------------------------------ */
        if (nextServer == Team.Default)
        {
            // first ever serve – pick a random side
            ballSpawnSide = (Random.Range(0, 2) == 0) ? -1 : 1;
            nextServer = (ballSpawnSide == -1) ? Team.Blue : Team.Red;
        }
        else
        {
            // normal case – use the team that just scored
            ballSpawnSide = (nextServer == Team.Blue) ? 1 : -1;
        }

        float z = (ballSpawnSide == -1) ? -4f : 4f;   // Blue court is -Z
        ball.transform.localPosition = new Vector3(0f, 2.5f, z);

        /* ------------------------------------------------
         * 2)  Clear motion (must be non-kinematic)
         * ------------------------------------------------ */
        ballRb.isKinematic = false;
        ballRb.linearVelocity = Vector3.zero;       // <- correct property name
        ballRb.angularVelocity = Vector3.zero;

        /* ------------------------------------------------
         * 3)  Freeze or release depending on freezeOnServe
         * ------------------------------------------------ */
        D($"ResetBall – server={nextServer}  spawnZ={(ballSpawnSide == -1 ? "-7(Blue)" : "+7(Red)")}");

        if (freezeOnServe)
        {
            isBallFrozen = true;
            ballRb.useGravity = false;
            ballRb.isKinematic = true;
            ballCol.isTrigger = true;   // no solid contact during freeze
        }
        else
        {
            isBallFrozen = false;
            ballRb.useGravity = true;
            ballRb.isKinematic = false;
            ballCol.isTrigger = false;
        }

        Physics.SyncTransforms();
    }
}
