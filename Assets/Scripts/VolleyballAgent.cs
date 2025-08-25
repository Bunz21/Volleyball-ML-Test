using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.InputSystem;

public enum Role
{
    Passer,
    Hitter,
    Generic
}

public class VolleyballAgent : Agent
{
    [SerializeField] private GameObject area;
    [SerializeField] private VolleyballAgent teammate; // assign in Inspector, or find at runtime
    Rigidbody agentRb;
    BehaviorParameters behaviorParameters;
    public Team teamId;
    [HideInInspector] public Role role = Role.Generic;

    // To get ball's location for observations
    public GameObject ball;
    Rigidbody ballRb;
    [SerializeField] private VolleyballSettings volleyballSettings; // allow Inspector hookup
    [HideInInspector] public EnvironmentController envController;   // <-- already there? keep it.

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

    new void Awake()
    {
        // If you forgot to drag the reference in the Inspector, grab the first one up the hierarchy.
        if (envController == null)
            envController = GetComponentInParent<EnvironmentController>();

        // Optional: fallback warning
        if (envController == null)
            Debug.LogError($"[{name}] could not find EnvironmentController in parents!");

        base.Awake();
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
    /// Called when agent collides with the something
    /// </summary>
    // Called when agent collides with the something
    void OnCollisionEnter(Collision c)
    {
        //  ---   BALL TOUCH   --------------------------------------------------
        if (c.collider.CompareTag("ball"))
        {
            if (envController != null)
            {
                envController.RegisterTouch(this);
                // after you confirm it's a *legal* touch
                //Vector3 courtFwd = (this.teamId == Team.Blue) ? Vector3.back : Vector3.forward;
                //float dirScore = Vector3.Dot(ballRb.linearVelocity.normalized, courtFwd);   // –1..1
                //float speedScore = Mathf.Clamp01(ballRb.linearVelocity.magnitude / 15f);      // 0..1

                //float touchReward = 0.05f * dirScore * speedScore;
                //AddReward(touchReward);

                AddReward(0.05f);
            }
            return;
        }

        //  ---   TEAM-MATE BUMP   ----------------------------------------------
        // “CompareTag("agent")” is the cheapest, but you can also
        // check `c.collider.GetComponent<VolleyballAgent>()` if you prefer
        if (c.collider.CompareTag("blueAgent") || c.collider.CompareTag("redAgent"))
        {
            // Make sure it’s the *teammate*, not an opponent
            if (teammate != null && c.collider.gameObject == teammate.gameObject)
            {
                AddReward(-0.02f);
            }
        }
    }

    void OnCollisionStay(Collision c)
    {
        if (c.collider.CompareTag("blueAgent") || c.collider.CompareTag("redAgent") &&
            teammate != null && c.collider.gameObject == teammate.gameObject)
        {
            AddReward(-0.03f * 0.2f); // small drain each physics step
        }
    }




    /// <summary>
    /// Starts the jump sequence
    /// </summary>
    public void Jump()
    {
        AddReward(-0.000025f);
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

    public override void OnActionReceived(ActionBuffers actionBuffers)
    {
        const float airPenaltyPerStep = 0.0001f;   // tune as you like

        bool grounded = CheckIfGrounded();
        if (!grounded)
            AddReward(-airPenaltyPerStep);

        AddReward(-0.00005f);

        if (role == Role.Hitter && teammate != null)
        {
            float dist = Vector3.Distance(transform.position, teammate.transform.position);
            if (dist < 0.8f) AddReward(-0.0001f);   // crowding
            else if (dist > 1.2f) AddReward(+0.0002f);   // good spacing
        }
        else if (role == Role.Passer && teammate != null)
        {
            float dist = Vector3.Distance(transform.position, envController.net.transform.position);
            if (dist < 0.8f) AddReward(-0.0001f);   // crowding
            else if (dist > 1.2f) AddReward(+0.0002f);   // good spacing
        }

        MoveAgent(actionBuffers.DiscreteActions);
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // --- tiny local toggle/throttle (change here if you want) ---
        const bool LOG_OBS = false;          // set false to silence
        const int LOG_EVERY_N_STEPS = 1;    // 1 = log every decision

        // Local buffer + lightweight wrappers so we don't touch the rest of your class
        System.Text.StringBuilder _sb = null;
        System.Collections.Generic.List<float> _obs = null;

        void AddF(float f)
        {
            sensor.AddObservation(f);
            if (LOG_OBS) _obs.Add(f);
        }
        void AddV(Vector3 v)
        {
            sensor.AddObservation(v);
            if (LOG_OBS) { _obs.Add(v.x); _obs.Add(v.y); _obs.Add(v.z); }
        }

        //----------------------------------------------------------------------
        // 0.  CONSTANTS / PREP
        //----------------------------------------------------------------------
        const float maxDist = 20f;                        // you asked for 20
        Vector3 envOrigin = area.transform.position;      // centre of *this* court

        if (LOG_OBS)
        {
            if (_obs == null) _obs = new System.Collections.Generic.List<float>(64);
            _obs.Clear();
            if (_sb == null) _sb = new System.Text.StringBuilder(512);
            _sb.Clear();
        }

        //----------------------------------------------------------------------
        // 1.  AGENT & BALL  (-- already in your code --)
        //----------------------------------------------------------------------
        float yawDeg = transform.eulerAngles.y;
        float yawNorm = Mathf.DeltaAngle(0f, yawDeg) / 180f;
        AddF(yawNorm);

        Vector3 toBall = new Vector3(
            (ballRb.position.x - transform.position.x) * agentRot,
            (ballRb.position.y - transform.position.y),
            (ballRb.position.z - transform.position.z) * agentRot
        );
        Vector3 dirToBall = toBall.sqrMagnitude > 1e-6f ? toBall.normalized : Vector3.zero;
        AddV(dirToBall);
        AddF(Mathf.Clamp01(toBall.magnitude / maxDist));

        // 2.  SELF & BALL VELOCITIES  (unchanged)
        AddV(agentRb.linearVelocity / 15f);
        Vector3 bv = ballRb.linearVelocity / 15f;
        AddF(bv.y);
        AddF(bv.z * agentRot);
        AddF(bv.x * agentRot);

        Vector3 landWorld = PredictLanding(ballRb);         // world coords
        Vector3 landRel = new Vector3(
                (landWorld.x - transform.position.x) * agentRot,
                (landWorld.y - transform.position.y),
                (landWorld.z - transform.position.z) * agentRot
            );
        landRel.y = 0;

        Vector3 landDir = landRel.sqrMagnitude > 1e-6f ? landRel.normalized : Vector3.zero;
        AddV(landDir);                                      // 3 floats
        AddF(Mathf.Clamp01(landRel.magnitude / maxDist));   // 1 float

        // 3.  TEAM-MATE INFO  (unchanged, your block here)
        if (teammate != null)
        {
            Vector3 toMate = new Vector3(
                (teammate.transform.position.x - transform.position.x) * agentRot,
                (teammate.transform.position.y - transform.position.y),
                (teammate.transform.position.z - transform.position.z) * agentRot
            );

            Vector3 mateDir = toMate.sqrMagnitude > 1e-6f ? toMate.normalized : Vector3.zero;
            AddV(mateDir); // 3

            Vector3 mateVel = teammate.GetComponent<Rigidbody>().linearVelocity / 10f;
            // mirror x/z for symmetry
            AddF(mateVel.y);
            AddF(mateVel.z * agentRot);
            AddF(mateVel.x * agentRot);
        }
        else
        {
            // pad if null (keeps obs size constant)
            AddV(Vector3.zero); // dir
            AddF(0f); // vy
            AddF(0f); // vz
            AddF(0f); // vx
        }

        //----------------------------------------------------------------------
        // 4.  NEW:  NET + GOALS
        //----------------------------------------------------------------------
        // Convert important points to *local* coordinates of the court prefab
        Vector3 agentLocal = transform.position - envOrigin;
        Vector3 netLocal = envController.net.transform.position - envOrigin;
        Vector3 blueGoal = envController.blueGoal.transform.position - envOrigin;   // z = -5
        Vector3 redGoal = envController.redGoal.transform.position - envOrigin;     // z =  5

        Vector3 ownGoalLocal = (teamId == Team.Blue) ? blueGoal : redGoal;
        Vector3 opponentGoalLocal = (teamId == Team.Blue) ? redGoal : blueGoal;

        // --- Helper: add 3-float direction + 1-float distance
        void AddDirAndDist(Vector3 targetLocal)
        {
            Vector3 toTgt = new Vector3(
                (targetLocal.x - agentLocal.x) * agentRot,
                (targetLocal.y - agentLocal.y),
                (targetLocal.z - agentLocal.z) * agentRot
            );
            toTgt.y = 0;
            Vector3 dir = toTgt.sqrMagnitude > 1e-6f ? toTgt.normalized : Vector3.zero;
            AddV(dir);                                     // 3 floats
            AddF(Mathf.Clamp01(toTgt.magnitude / maxDist)); // 1 float
        }

        AddDirAndDist(netLocal);          // 4 floats
        AddDirAndDist(ownGoalLocal);      // 4 floats
        AddDirAndDist(opponentGoalLocal); // 4 floats
                                          //  >>> +12 floats total

        AddF(role == Role.Passer ? 1f : 0f);
        AddF(role == Role.Hitter ? 1f : 0f);

        float lastTouchFlag = (envController.lastHitterAgent == this) ? 1f :
                       (envController.lastHitterAgent == teammate) ? -1f : 0f;
        AddF(lastTouchFlag);

        //----------------------------------------------------------------------
        // 5.  TOUCHES & GROUNDED FLAG  (unchanged)
        //----------------------------------------------------------------------
        float touchesUsed = (teamId == Team.Blue)
                            ? envController.touchesBlue
                            : envController.touchesRed;
        AddF(Mathf.Clamp01(touchesUsed / 3f));
        AddF(CheckIfGrounded() ? 1f : 0f);

        // --- compact debug line at the end (throttled) ---
        if (LOG_OBS && (LOG_EVERY_N_STEPS <= 1 || StepCount % LOG_EVERY_N_STEPS == 0))
        {
            _sb.Append("[Team=").Append(teamId)
               .Append(" Agent=").Append(gameObject.name)
               .Append("] ");
            _sb.Append("step=").Append(StepCount)
               .Append(" n=").Append(_obs.Count)
               .Append(" role=").Append(role)
               .Append(" yawNorm=").Append(yawNorm.ToString("G4"))
               .Append(" dirBall=(").Append(dirToBall.x.ToString("G4")).Append(",")
                                    .Append(dirToBall.y.ToString("G4")).Append(",")
                                    .Append(dirToBall.z.ToString("G4")).Append(")")
               .Append(" distBallNorm=").Append(Mathf.Clamp01(toBall.magnitude / maxDist).ToString("G4"))
               .Append(" grounded=").Append(CheckIfGrounded() ? "1" : "0")
               .Append(" touchesUsed=").Append(touchesUsed);

            // Full vector logging (optional)
            _sb.Append(" obs=[");
            for (int i = 0; i < _obs.Count; i++) { if (i > 0) _sb.Append(','); _sb.Append(_obs[i].ToString("G6")); }
            _sb.Append(']');

            UnityEngine.Debug.Log(_sb.ToString());
        }
    }

    Vector3 PredictLanding(Rigidbody rb)
    {
        // simple parabola: y = y0 + v0y t – ½ g t²  set y=0
        float vy = rb.linearVelocity.y;
        float y0 = rb.position.y;
        float t = (vy + Mathf.Sqrt(vy * vy + 2 * Physics.gravity.y * -y0))
                   / -Physics.gravity.y;   // positive root
        return rb.position + rb.linearVelocity * t + 0.5f * Physics.gravity * t * t;
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
