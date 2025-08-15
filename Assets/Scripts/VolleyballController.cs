using System.Collections.Generic;
using UnityEngine;

public class VolleyballBallController : MonoBehaviour
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
    public bool inMiniServe = false;
    public bool InMiniServe => inMiniServe;


    void Start()
    {
        rb = GetComponent<Rigidbody>();
        envController = area.GetComponent<EnvironmentController>();
    }

    void FixedUpdate()
    {
        // Optional: Apply custom gravity multiplier
        rb.AddForce(Physics.gravity * (gravityScale - 1f), ForceMode.Acceleration);
    }

    /// <summary>
    /// Called to apply a ball hit (pass, set, spike)
    /// </summary>
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

        rb.linearVelocity = Vector3.zero; // Reset current velocity
        rb.AddForce(direction.normalized * force, ForceMode.VelocityChange);

        lastHitterTeam = hittingTeam;

        if (hitType != HitType.Block)
        {
            touchesThisSide++;
        }
    }

    void OnCollisionEnter(Collision col)
    {
        // Detect floor zones for scoring
        if (col.gameObject.CompareTag("FloorBlue"))
        {
            envController.PointScored(Team.Red);
            ResetBallMiniServe(Team.Red);
        }
        else if (col.gameObject.CompareTag("FloorRed"))
        {
            envController.PointScored(Team.Blue);
            ResetBallMiniServe(Team.Blue);
        }

        // Detect net collision
        if (col.gameObject.CompareTag("Net"))
        {
            // Optional: could reduce velocity or trigger special effects
        }

        // Detect out zones
        if (col.gameObject.CompareTag("OutZone"))
        {
            // Point to opposing team if ball went out
            Team scoringTeam = (lastHitterTeam == Team.Blue) ? Team.Red : Team.Blue;
            envController.PointScored(scoringTeam);
            ResetBallMiniServe(scoringTeam);
        }
    }

    /// <summary>
    /// Resets the ball to center and applies a small mini-serve force toward the team that scored
    /// </summary>
    public void ResetBallMiniServe(Team servingTeam)
    {
        touchesThisSide = 0;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        transform.position = Vector3.zero + Vector3.up * 0.5f; // spawn slightly above floor

        inMiniServe = true;

        // Push toward serving side
        Vector3 serveDir = servingTeam == Team.Blue ? Vector3.forward : Vector3.back;
        rb.AddForce(serveDir * miniServeForce, ForceMode.VelocityChange);
    }

    /// <summary>
    /// Reset counters after ball crosses to other side or after point
    /// </summary>
    public void ResetTouches()
    {
        touchesThisSide = 0;
    }

    public void EndMiniServe()
    {
        inMiniServe = false;
    }

}

public enum HitType
{
    Pass,
    Set,
    Spike,
    Block
}
