using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.InputSystem;

public class VolleyballAgent : Agent
{
    [SerializeField] private GameObject area;
    Rigidbody agentRb;
    BehaviorParameters behaviorParameters;
    public Team teamId;

    // To get ball's location for observations
    public GameObject ball;
    Rigidbody ballRb;

    [SerializeField] private VolleyballSettings volleyballSettings; // allow Inspector hookup
    EnvironmentController envController;

    // Controls jump behavior
    float jumpingTime;
    Vector3 jumpTargetPos;
    Vector3 jumpStartingPos;
    float agentRot;

    public Collider[] hitGroundColliders = new Collider[3];
    EnvironmentParameters resetParams;

    void Start()
    {
        envController = area.GetComponent<EnvironmentController>();
    }

    public override void Initialize()
    {
        volleyballSettings = FindFirstObjectByType<VolleyballSettings>();
        behaviorParameters = gameObject.GetComponent<BehaviorParameters>();

        agentRb = GetComponent<Rigidbody>();
        ballRb = ball.GetComponent<Rigidbody>();

        // for symmetry between player side
        if (teamId == Team.Blue)
        {
            agentRot = -1;
        }
        else
        {
            agentRot = 1;
        }

        resetParams = Academy.Instance.EnvironmentParameters;
    }

    /// <summary>
    /// Moves  a rigidbody towards a position smoothly.
    /// </summary>
    /// <param name="targetPos">Target position.</param>
    /// <param name="rb">The rigidbody to be moved.</param>
    /// <param name="targetVel">The velocity to target during the
    ///  motion.</param>
    /// <param name="maxVel">The maximum velocity posible.</param>
    void MoveTowards(
        Vector3 targetPos, Rigidbody rb, float targetVel, float maxVel)
    {
        var moveToPos = targetPos - rb.worldCenterOfMass;
        var velocityTarget = Time.fixedDeltaTime * targetVel * moveToPos;
        if (float.IsNaN(velocityTarget.x) == false)
        {
            rb.linearVelocity = Vector3.MoveTowards(
                rb.linearVelocity, velocityTarget, maxVel);
        }
    }

    /// <summary>
    /// Check if agent is on the ground to enable/disable jumping
    /// </summary>
    public bool CheckIfGrounded()
    {
        hitGroundColliders = new Collider[3];
        var o = gameObject;
        Physics.OverlapBoxNonAlloc(
            o.transform.localPosition + new Vector3(0, -0.05f, 0),
            new Vector3(0.95f / 2f, 0.5f, 0.95f / 2f),
            hitGroundColliders,
            o.transform.rotation);
        var grounded = false;
        foreach (var col in hitGroundColliders)
        {
            if (col != null && col.transform != transform &&
                (col.CompareTag("outofbounds") ||
                 col.CompareTag("floorRed") ||
                 col.CompareTag("floorBlue")))
            {
                grounded = true; //then we're grounded
                break;
            }
        }
        return grounded;
    }

    /// <summary>
    /// Called when agent collides with the ball
    /// </summary>
    // Called when agent collides with the ball
    void OnCollisionEnter(Collision c)
    {
        if (!c.collider.CompareTag("ball")) return;
        if (envController != null)
        {
            envController.RegisterTouch(this);  // <- NEW
        }
        //if (teamId == Team.Blue)
        //{
        //    if (c.collider.CompareTag("floorRed"))
        //    {
        //        AddReward(-0.005f);
        //    }
        //} else if (teamId == Team.Red)
        //{
        //    if (c.collider.CompareTag("floorBlue"))
        //    {
        //        AddReward(-0.005f);
        //    }
        //}
    }


    /// <summary>
    /// Starts the jump sequence
    /// </summary>
    public void Jump()
    {
        jumpingTime = 0.2f;
        jumpStartingPos = agentRb.position;
    }

    /// <summary>
    /// Resolves the agent movement
    /// </summary>
    public void MoveAgent(ActionSegment<int> act)
    {
        if (volleyballSettings == null || agentRb == null)
        {
            Debug.LogError("[VolleyballAgent] Null refs in MoveAgent (settings or rb).", this);
            return;
        }

        var grounded = CheckIfGrounded();
        var dirToGo = Vector3.zero;
        var rotateDir = Vector3.zero;
        var dirToGoForwardAction = act[0];
        var rotateDirAction = act[1];
        var dirToGoSideAction = act[2];
        var jumpAction = act[3];

        if (dirToGoForwardAction == 1)
            dirToGo = (grounded ? 1f : 0.5f) * transform.forward * 1f;
        else if (dirToGoForwardAction == 2)
            dirToGo = (grounded ? 1f : 0.5f) * transform.forward * volleyballSettings.speedReductionFactor * -1f;

        if (rotateDirAction == 1)
            rotateDir = transform.up * -1f;
        else if (rotateDirAction == 2)
            rotateDir = transform.up * 1f;

        if (dirToGoSideAction == 1)
            dirToGo = (grounded ? 1f : 0.5f) * transform.right * volleyballSettings.speedReductionFactor * -1f;
        else if (dirToGoSideAction == 2)
            dirToGo = (grounded ? 1f : 0.5f) * transform.right * volleyballSettings.speedReductionFactor;

        if (jumpAction == 1)
            if (((jumpingTime <= 0f) && grounded))
            {
                Jump();
            }

        transform.Rotate(rotateDir, Time.fixedDeltaTime * 200f);
        agentRb.AddForce(dirToGo * volleyballSettings.agentRunSpeed,
            ForceMode.VelocityChange);

        if (jumpingTime > 0f)
        {
            jumpTargetPos =
                new Vector3(agentRb.position.x,
                    jumpStartingPos.y + volleyballSettings.agentJumpHeight,
                    agentRb.position.z) + dirToGo;

            MoveTowards(jumpTargetPos, agentRb, volleyballSettings.agentJumpVelocity,
                volleyballSettings.agentJumpVelocityMaxChange);
        }

        if (!(jumpingTime > 0f) && !grounded)
        {
            agentRb.AddForce(
                Vector3.down * volleyballSettings.fallingForce, ForceMode.Acceleration);
        }

        if (jumpingTime > 0f)
        {
            jumpingTime -= Time.fixedDeltaTime;
        }
    }

    ///// <summary>
    ///// Resolves the agent movement
    ///// </summary>
    //public void MoveAgent(ActionSegment<int> act)
    //{
    //    var grounded = CheckIfGrounded();
    //    var dirToGo = Vector3.zero;
    //    var rotateDir = Vector3.zero;
    //    var dirToGoForwardAction = act[0];
    //    var rotateDirAction = act[1];
    //    var dirToGoSideAction = act[2];
    //    var jumpAction = act[3];

    //    if (dirToGoForwardAction == 1)
    //        dirToGo = (grounded ? 1f : 0.5f) * transform.forward * 1f;
    //    else if (dirToGoForwardAction == 2)
    //        dirToGo = (grounded ? 1f : 0.5f) * transform.forward * volleyballSettings.speedReductionFactor * -1f;

    //    if (rotateDirAction == 1)
    //        rotateDir = transform.up * -1f;
    //    else if (rotateDirAction == 2)
    //        rotateDir = transform.up * 1f;

    //    if (dirToGoSideAction == 1)
    //        dirToGo = (grounded ? 1f : 0.5f) * transform.right * volleyballSettings.speedReductionFactor * -1f;
    //    else if (dirToGoSideAction == 2)
    //        dirToGo = (grounded ? 1f : 0.5f) * transform.right * volleyballSettings.speedReductionFactor;

    //    if (jumpAction == 1)
    //        if (((jumpingTime <= 0f) && grounded))
    //        {
    //            Jump();
    //        }

    //    transform.Rotate(rotateDir, Time.fixedDeltaTime * 200f);
    //    agentRb.AddForce(dirToGo * volleyballSettings.agentRunSpeed,
    //        ForceMode.VelocityChange);

    //    if (jumpingTime > 0f)
    //    {
    //        jumpTargetPos =
    //            new Vector3(agentRb.position.x,
    //                jumpStartingPos.y + volleyballSettings.agentJumpHeight,
    //                agentRb.position.z) + dirToGo;

    //        MoveTowards(jumpTargetPos, agentRb, volleyballSettings.agentJumpVelocity,
    //            volleyballSettings.agentJumpVelocityMaxChange);
    //    }

    //    if (!(jumpingTime > 0f) && !grounded)
    //    {
    //        agentRb.AddForce(
    //            Vector3.down * volleyballSettings.fallingForce, ForceMode.Acceleration);
    //    }

    //    if (jumpingTime > 0f)
    //    {
    //        jumpingTime -= Time.fixedDeltaTime;
    //    }
    //}

    public override void OnActionReceived(ActionBuffers actionBuffers)
    {
        AddReward(-0.0005f);
        MoveAgent(actionBuffers.DiscreteActions);
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // 1) Proper yaw in [-1, 1] instead of quaternion.y
        float yawDeg = transform.eulerAngles.y;                  // 0..360
        float yawNorm = Mathf.DeltaAngle(0f, yawDeg) / 180f;     // -1..1
        sensor.AddObservation(yawNorm);                          // 1 float

        // 2) Direction to ball (mirrored by agentRot) + distance
        Vector3 toBall = new Vector3(
            (ballRb.position.x - transform.position.x) * agentRot,
            (ballRb.position.y - transform.position.y),
            (ballRb.position.z - transform.position.z) * agentRot
        );
        Vector3 dirToBall = toBall.sqrMagnitude > 1e-6f ? toBall.normalized : Vector3.zero;
        sensor.AddObservation(dirToBall);                        // 3 floats
        sensor.AddObservation(toBall.magnitude);                 // 1 float

        // Agent velocity (use Rigidbody.velocity, not linearVelocity)
        sensor.AddObservation(agentRb.linearVelocity);                 // 3 floats

        // Ball velocity (mirror x/z with agentRot for symmetry)
        Vector3 bv = ballRb.linearVelocity;
        sensor.AddObservation(bv.y);                             // 1
        sensor.AddObservation(bv.z * agentRot);                  // 1
        sensor.AddObservation(bv.x * agentRot);                  // 1
    }


    // For human controller
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var da = actionsOut.DiscreteActions;

        // zero everything first (important when mixing keys)
        for (int i = 0; i < da.Length; i++) da[i] = 0;

        var k = Keyboard.current;
        if (k == null) return; // no keyboard attached

        // rotate (branch 1)
        if (k.dKey.isPressed) da[1] = 2;          // rotate right
        else if (k.aKey.isPressed) da[1] = 1;     // rotate left

        // forward/back (branch 0)
        if (k.wKey.isPressed || k.upArrowKey.isPressed) da[0] = 1;     // forward
        else if (k.sKey.isPressed || k.downArrowKey.isPressed) da[0] = 2; // back

        // strafe (branch 2)
        if (k.rightArrowKey.isPressed) da[2] = 2; // move right
        else if (k.leftArrowKey.isPressed) da[2] = 1; // move left

        // jump/spike/block (branch 3)
        if (k.spaceKey.isPressed) da[3] = 1;      // jump
                                                  // (extend with other keys for spike/block if needed)
    }

}
