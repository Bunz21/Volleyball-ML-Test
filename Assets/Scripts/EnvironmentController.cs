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
    int ballSpawnSide;

    // Add near top of EnvironmentController
    [SerializeField] float velocityRewardForwardWeight = 0.02f;  // per m/s toward opponent
    [SerializeField] float velocityRewardDownWeight = 0.03f;  // per m/s downward
    [SerializeField] float velocityRewardMax = 0.4f;   // cap

    VolleyballSettings volleyballSettings;

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
    void Awake()
    {
        if (ballRb == null && ball != null) ballRb = ball.GetComponent<Rigidbody>();
    }

    void Start()
    {

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

    /// <summary>
    /// Tracks which agent last had control of the ball
    /// </summary>
    public void UpdateLastHitter(Team team)
    {
        lastHitter = team;
    }

    public void AwardFaultAgainstLastHitter()
    {
        if (lastHitter == Team.Blue)
        {
            blueAgent.AddReward(-1f);
            redAgent.AddReward(1f);
        }
        else if (lastHitter == Team.Red)
        {
            redAgent.AddReward(-1f);
            blueAgent.AddReward(1f);
        }
        else
        {
            // No known last hitter; treat as neutral or choose a policy. Here we do no-op.
        }

        // End rally
        blueAgent.EndEpisode();
        redAgent.EndEpisode();

        // Clear rally state
        ballPassedOverNet = false;
        ResetScene();
    }

    public void RegisterTouch(VolleyballAgent agent)
    {
        if (agent == null) return;
        ballPassedOverNet = false;

        // Cooldown: ignore rapid repeated contacts from same agent
        float now = Time.time;
        if (lastTouchTime.TryGetValue(agent, out float tLast) && (now - tLast) < touchCooldown)
            return;
        lastTouchTime[agent] = now;

        // Double-touch check: same agent touching twice in a row
        if (lastHitterAgent == agent)
        {
            // Double-touch fault: award point to the OTHER team
            if (agent.teamId == Team.Blue)
            {
                blueAgent.AddReward(-1f);
                redAgent.AddReward(1f);
            }
            else if (agent.teamId == Team.Red)
            {
                blueAgent.AddReward(1f);
                redAgent.AddReward(-1f);
            }
            ResetScene();
            return;
        }

        // New legal touch: update last hitter and increment that team's counter
        lastHitterAgent = agent;
        lastHitterTeam = agent.teamId;

        if (agent.teamId == Team.Blue) touchesBlue++;
        else touchesRed++;

        // Exceeded 3 touches on a side -> fault to opponent
        if (touchesBlue > 3)
        {
            blueAgent.AddReward(-1f);
            redAgent.AddReward(1f);
            return;
        }
        if (touchesRed > 3)
        {
            blueAgent.AddReward(1f);
            redAgent.AddReward(-1f);
            return;
        }
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
                {
                    // Last team to hit loses the point (full point)
                    AwardFaultAgainstLastHitter();
                    break;
                }

            case Event.HitRedGoal:
                {
                    // Ball hit Red floor. Only award Blue if the ball crossed the net this rally.
                    if (ballPassedOverNet)
                    {
                        blueAgent.AddReward(1f);
                        redAgent.AddReward(-1f);

                        // >>> velocity-based bonus for Blue
                        if (ballRb != null)
                        {
                            Vector3 v = ballRb.linearVelocity;
                            float forward = Mathf.Max(0f, Vector3.Dot(v, Vector3.forward)); // +Z toward Red
                            float down = Mathf.Max(0f, -v.y);                             // only downward
                            float bonus = velocityRewardForwardWeight * forward
                                          + velocityRewardDownWeight * down;
                            bonus = Mathf.Min(bonus, velocityRewardMax);
                            if (bonus > 0f) blueAgent.AddReward(bonus);
                        }

                        // Visual feedback (Blue scored)
                        StartCoroutine(GoalScoredSwapGroundMaterial(volleyballSettings.blueGoalMaterial, RenderersList, 0.5f));

                        blueAgent.EndEpisode();
                        redAgent.EndEpisode();

                        ballPassedOverNet = false;
                        ResetScene();
                    }
                    else
                    {
                        blueAgent.AddReward(1f);
                        redAgent.AddReward(-1f);
                        ResetScene();
                    }
                    break;
                }

            case Event.HitBlueGoal:
                {
                    // Ball hit Blue floor. Only award Red if the ball crossed the net this rally.
                    if (ballPassedOverNet)
                    {
                        redAgent.AddReward(1f);
                        blueAgent.AddReward(-1f);

                        // >>> velocity-based bonus for Red
                        if (ballRb != null)
                        {
                            Vector3 v = ballRb.linearVelocity;
                            float forward = Mathf.Max(0f, Vector3.Dot(v, Vector3.back)); // -Z toward Blue
                            float down = Mathf.Max(0f, -v.y);
                            float bonus = velocityRewardForwardWeight * forward
                                          + velocityRewardDownWeight * down;
                            bonus = Mathf.Min(bonus, velocityRewardMax);
                            if (bonus > 0f) redAgent.AddReward(bonus);
                        }


                        // Visual feedback (Red scored)
                        StartCoroutine(GoalScoredSwapGroundMaterial(volleyballSettings.redGoalMaterial, RenderersList, 0.5f));

                        blueAgent.EndEpisode();
                        redAgent.EndEpisode();

                        ballPassedOverNet = false;
                        ResetScene();
                    }
                    else
                    {
                        redAgent.AddReward(1f);
                        blueAgent.AddReward(-1f);
                        ResetScene();
                    }
                    break;
                }

            case Event.PassOverNet:
                {
                    // Mark that a legal crossing occurred; optional small shaping reward to last hitter
                    ballPassedOverNet = true;
                    touchesBlue = 0;
                    touchesRed = 0;
                    lastHitterAgent = null;

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
                    break;
                }

            case Event.AgentTouch:
                {
                    ballPassedOverNet = false;
                    break;
                }
        }
    }

    /// <summary>
    /// Changes the color of the ground for a moment.
    /// </summary>
    /// <returns>The Enumerator to be used in a Coroutine.</returns>
    /// <param name="mat">The material to be swapped.</param>
    /// <param name="time">The time the material will remain.</param>
    IEnumerator GoalScoredSwapGroundMaterial(Material mat, List<Renderer> rendererList, float time)
    {
        foreach (var renderer in rendererList)
        {
            renderer.material = mat;
        }

        yield return new WaitForSeconds(time); // wait for 2 sec

        foreach (var renderer in rendererList)
        {
            renderer.material = volleyballSettings.defaultMaterial;
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
        resetTimer = 0;
        lastHitter = Team.Default;
        ballPassedOverNet = false;
        touchesBlue = 0;
        touchesRed = 0;
        lastHitterAgent = null;
        lastHitter = Team.Default;
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
        // alternate ball spawn side
        // -1 = spawn blue side, 1 = spawn red side
        ballSpawnSide = -1 * ballSpawnSide;

        if (ballSpawnSide == -1)
        {
            // Blue side spawn
            ball.transform.localPosition = new Vector3(0f, 10f, -4f);
        }
        else if (ballSpawnSide == 1)
        {
            // Red side spawn
            ball.transform.localPosition = new Vector3(0f, 10f, 4f);
        }

        // Reset ball physics
        ballRb.angularVelocity = Vector3.zero;
        ballRb.linearVelocity = Vector3.zero;
    }
}
