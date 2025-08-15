using UnityEngine;

public class VolleyballController : MonoBehaviour
{
    [Header("References")]
    public GameObject area;
    [HideInInspector] public EnvironmentController envController;

    [Header("Ball Settings")]
    public float passForce = 5f;
    public float setForce = 2f;
    public float spikeForce = 8f;
    public float miniServeForce = 3f;
    public float gravityScale = 1f;

    [Header("Tracking")]
    public Team lastHitterTeam;
    public int touchesThisSide = 0;
    public int maxTouchesPerSide = 3;

    private Rigidbody rb;
    private bool inMiniServe = false;
    private bool ballPassedOverNet = false;

    [Header("Debug Options")]
    public bool debugForceServe = true;
    public Team debugServingTeam = Team.Blue;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        envController = area.GetComponent<EnvironmentController>();
    }

    void FixedUpdate()
    {
        rb.AddForce(Physics.gravity * (gravityScale - 1f), ForceMode.Acceleration);
    }

    public void HitBall(Vector3 direction, HitType hitType, Team hittingTeam, float powerModifier = 1f)
    {
        float force = hitType switch
        {
            HitType.Pass => passForce,
            HitType.Set => setForce,
            HitType.Spike => spikeForce,
            _ => passForce
        };

        force *= powerModifier;

        rb.linearVelocity = Vector3.zero;
        rb.AddForce(direction.normalized * force, ForceMode.VelocityChange);

        lastHitterTeam = hittingTeam;

        if (hitType != HitType.Block)
            touchesThisSide++;
    }

    void OnCollisionEnter(Collision col)
    {
        string tag = col.gameObject.tag;

        // Ball hits floor
        if (tag == "FloorBlue")
        {
            HandleFloorHit(Team.Red);
        }
        else if (tag == "FloorRed")
        {
            HandleFloorHit(Team.Blue);
        }

        // Ball hits out-of-bounds or antenna
        else if (tag == "OutOfBounds" || tag == "Antenna")
        {
            Team scoringTeam = GetOpposingTeam(lastHitterTeam);
            envController.PointScored(scoringTeam);
            ResetBallMiniServe(scoringTeam);
        }

        // Ball passes over net
        else if (tag == "OverNetDetector")
        {
            ballPassedOverNet = true;
        }

        // Ball hits OFB detector after passing over net
        else if (tag == "OFBDetector")
        {
            if (ballPassedOverNet)
            {
                Team scoringTeam = GetOpposingTeam(lastHitterTeam);
                envController.PointScored(scoringTeam);
                ResetBallMiniServe(scoringTeam);
            }
            else
            {
                // If ball never passed over net but hit wall
                Team scoringTeam = GetOpposingTeam(lastHitterTeam);
                envController.PointScored(scoringTeam);
                ResetBallMiniServe(scoringTeam);
            }
        }
    }

    void HandleFloorHit(Team fieldTeam)
    {
        if (ballPassedOverNet)
        {
            envController.PointScored(fieldTeam); // Scoring team
        }
        else
        {
            Team scoringTeam = GetOpposingTeam(lastHitterTeam);
            envController.PointScored(scoringTeam);
        }

        ResetBallMiniServe(fieldTeam);
    }

    Team GetOpposingTeam(Team team) => team == Team.Blue ? Team.Red : Team.Blue;

    public void ResetBallMiniServe(Team servingTeam = Team.Blue)
    {
        touchesThisSide = 0;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        transform.position = Vector3.up * 0.5f;
        inMiniServe = true;
        ballPassedOverNet = false;

        // Determine actual serving team
        Team actualServingTeam;

        if (debugForceServe)
        {
            actualServingTeam = debugServingTeam;
        }
        else
        {
            actualServingTeam = (lastHitterTeam == Team.Blue || lastHitterTeam == Team.Red)
                ? servingTeam
                : (Random.value < 0.5f ? Team.Blue : Team.Red); // random at start
        }

        Vector3 serveDir = actualServingTeam == Team.Blue ? Vector3.forward : Vector3.back;
        rb.AddForce(serveDir * miniServeForce, ForceMode.VelocityChange);
    }

    public void ResetTouches()
    {
        touchesThisSide = 0;
    }

    public bool InMiniServe => inMiniServe;
}

public enum HitType
{
    Pass,
    Set,
    Spike,
    Block
}
