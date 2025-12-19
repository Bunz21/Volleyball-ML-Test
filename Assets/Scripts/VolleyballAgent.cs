using System.Collections.Generic;
using System.Linq;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.InputSystem;

public enum Role
{
    Passer,
    Setter,
    Hitter,
    Generic
}

public enum TouchType
{
    Bump,
    Set,
    Spike
}

public class VolleyballAgent : Agent
{
    // --- UNITY/ML-AGENTS FIELDS ---
    [SerializeField] private GameObject area;
    [SerializeField] private List<VolleyballAgent> teammates;
    [SerializeField] private List<VolleyballAgent> opponents;
    [SerializeField] private VolleyballSettings volleyballSettings;
    [HideInInspector] public EnvironmentController envController;
    [HideInInspector] public Role role = Role.Generic;
    const int MAX_TEAMMATES = 5;   // For 6 per team, not counting self
    const int MAX_OPPONENTS = 6;   // 6v6
    const int MAX_AGENTS = MAX_TEAMMATES + MAX_OPPONENTS + 1;
    public Team teamId;

    // --- COMPONENT REFERENCES ---
    public Rigidbody agentRb;
    private Renderer agentRenderer;
    private bool collisionStay = false;
    private Color defaultColor;
    private static readonly Color spikeColor = new Color(0.5f, 0f, 0.5f); // purple (R,G,B)
    private static readonly Color setColor = new Color(0f, 0.75f, 0f);
    private BehaviorParameters behaviorParameters;
    private EnvironmentParameters resetParams;

    // --- BALL REFERENCES ---
    public GameObject ball;   // To get ball's location for observations
    private Rigidbody ballRb;

    // --- GROUND COLLIDERS ---
    public Collider[] hitGroundColliders = new Collider[3];

    // --- JUMP/SPIKE CONTROLS ---
    private float jumpingTime;
    private Vector3 jumpTargetPos;
    private Vector3 jumpStartingPos;
    private float agentRot;
    private bool canSpike = false;
    public bool isSpiking = false;
    private float spikeTimer = 0f;
    [SerializeField] private float spikeWindow = 0.5f;

    // --- SET CONTROLS ---
    private bool canSet = true;
    public bool isSetting = false;
    private float setTimer = 0f;
    [SerializeField] private float setWindow = 0.5f;

    void Start()
    {
        envController = area.GetComponent<EnvironmentController>();

        agentRenderer = GetComponentInChildren<Renderer>(); // or your mesh renderer location
        if (agentRenderer != null)
            defaultColor = agentRenderer.material.color;
    }

    new void Awake()
    {
        // If you forgot to drag the reference in the Inspector, grab the first one up the hierarchy.
        if (envController == null)
            envController = GetComponentInParent<EnvironmentController>();

        // Optional: fallback warning
        if (envController == null)
            Debug.LogError($"[{name}] could not find EnvironmentController in parents!");

        teammates = envController.AgentsList.Where(a => a.teamId == this.teamId && a != this).ToList();
        opponents = envController.AgentsList.Where(a => a.teamId != this.teamId).ToList();

        base.Awake();
    }


    public override void Initialize()
    {
        volleyballSettings = FindFirstObjectByType<VolleyballSettings>();
        behaviorParameters = gameObject.GetComponent<BehaviorParameters>();

        agentRb = GetComponent<Rigidbody>();
        ballRb = ball.GetComponent<Rigidbody>();

        if (agentRenderer != null)
            agentRenderer.material.color = defaultColor;

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
        isSpiking = false;
        spikeTimer = 0f;
        canSpike = false;
        isSetting = false;
        setTimer = 0f;
        canSet = true;
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
    void OnCollisionEnter(Collision c)
    {
        //  ---   BALL TOUCH   --------------------------------------------------
        if (c.collider.CompareTag("ball"))
        {
            if (envController != null)
            {
                envController.RegisterTouch(this, DetermineTouchType(this));
            }

            if (isSpiking)
            {
                float spikePower = volleyballSettings.spikePower;

                // Use agent's forward direction but add downward angle
                Vector3 spikeDir = transform.forward;
                spikeDir.y = -0.25f; // Downward component
                spikeDir.Normalize();

                Vector3 spikeVelocity = spikeDir * spikePower;
                ballRb.linearVelocity = spikeVelocity;

                isSpiking = false;
                spikeTimer = 0f;
            }

            if (isSetting)
            {
                float setPower = volleyballSettings.setPower; // e.g., 5–7
                                                                // 15% forward, 99% upward (parabolic)
                Vector3 setDir = (transform.forward * 0.15f + Vector3.up * 1f).normalized;
                Vector3 setVelocity = setDir * setPower;

                ballRb.linearVelocity = setVelocity;

                isSetting = false;
                setTimer = 0f;
            }
        }

        if (c.collider.CompareTag("blueAgent") || c.collider.CompareTag("redAgent") && agentRb != null && c.collider.gameObject != agentRb.gameObject)
        {
            AddReward(-0.025f); // penalty on collision with teammate
            collisionStay = true;
        }
    }

    //void OnCollisionStay(Collision c)
    //{
    //    foreach (VolleyballAgent a in teammates)
    //    {
    //        if (c.collider.CompareTag("blueAgent") || c.collider.CompareTag("redAgent") &&
    //        a != null && c.collider.gameObject == a.gameObject)
    //        {
    //            AddReward(-0.03f); // small drain each physics step - doesn't work
    //        }
    //    }
    //}

    TouchType DetermineTouchType(VolleyballAgent agent)
    {
        return this.isSpiking ? TouchType.Spike :
               this.isSetting ? TouchType.Set :
               TouchType.Bump;
    }

    /// <summary>
    /// Starts the jump sequence
    /// </summary>
    public void Jump(bool isSpikeJump)
    {
        AddReward(-0.0075f);
        jumpingTime = isSpiking ? 0.25f : 0.2f; // Higher/faster jump for spike
        jumpStartingPos = agentRb.position;
        canSpike = isSpikeJump;
        isSpiking = isSpikeJump;   // Set spiking mode if spike jump
    }

    void AgentPositioning ()
    {
        // Optional: implement later
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
        var setAction = act[4];

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

        var jumpType = act[3];
        // 0 = no jump, 1 = normal, 2 = spike jump

        if ((jumpType == 1 || jumpType == 2) && jumpingTime <= 0f && grounded)
        {
            bool isSpikeJump = (jumpType == 2);
            Jump(isSpikeJump);
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
            agentRb.AddForce(Vector3.down * volleyballSettings.fallingForce, ForceMode.Acceleration);
        }

        if (!grounded && jumpAction == 2 && canSpike)
        {
            isSpiking = true;
            spikeTimer = spikeWindow;
            canSpike = false;  // Disable until next jump
        } else if (grounded && setAction == 1 && canSet)
        {
            isSetting = true;
            setTimer = setWindow;
            canSet = false;  // Disable until next grounded
        }

        // Cancel set if jump or spike initiated
        if (!grounded || jumpAction == 1 || jumpAction == 2 || isSpiking)
        {
            isSetting = false;
            setTimer = 0f;
        }

        // --- Clean canSet logic: ---
        // Always reset canSet when grounded, not setting, and not spiking
        if (grounded && !isSetting && !isSpiking)
            canSet = true;
    }

    public override void OnActionReceived(ActionBuffers actionBuffers)
    {
        const float airPenaltyPerStep = 0.0001f;   // Tweak as needed

        // --- Always reset isSpiking if grounded ---
        if (CheckIfGrounded())
            isSpiking = false;

        // --- Rewards ---
        if (!CheckIfGrounded())
            AddReward(-airPenaltyPerStep);

        //AddReward(-0.0005f); // Small time penalty TESTING

        // --- Timers ---
        if (jumpingTime > 0f)
            jumpingTime -= Time.fixedDeltaTime;

        // --- Spike State ---
        if (isSpiking && spikeTimer > 0f)
        {
            spikeTimer -= Time.fixedDeltaTime;
            if (spikeTimer <= 0f)
                isSpiking = false;
            spikeTimer = 0f;
        }

        // --- Set State ---
        if (isSetting && setTimer > 0f)
        {
            setTimer -= Time.fixedDeltaTime;
            if (setTimer <= 0f || !CheckIfGrounded())
            {
                isSetting = false;
                setTimer = 0f;
            }
        }

        // --- Visual feedback ---
        if (agentRenderer != null)
        {
            agentRenderer.material.color =
                isSpiking ? spikeColor :
                isSetting ? setColor :
                defaultColor;
        }

        // --- Set Ability Reset (when grounded and not setting and not spiking) ---
        if (CheckIfGrounded() && !isSetting && !isSpiking)
            canSet = true;

        // --- Collision stay penalty ---
        if (collisionStay)
        {
            AddReward(-0.005f); // small penalty for staying in collision
        }

        // --- Movement ---
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

        //Vector3 landWorld = PredictLanding(ballRb);         // world coords
        //Vector3 landRel = new Vector3(
        //        (landWorld.x - transform.position.x) * agentRot,
        //        (landWorld.y - transform.position.y),
        //        (landWorld.z - transform.position.z) * agentRot
        //    );
        //landRel.y = 0;

        //Vector3 landDir = landRel.sqrMagnitude > 1e-6f ? landRel.normalized : Vector3.zero;
        //AddV(landDir);                                      // 3 floats
        //AddF(Mathf.Clamp01(landRel.magnitude / maxDist));   // 1 float

        // 3.  TEAM-MATE INFO  (unchanged, your block here)
        for (int i = 0; i < MAX_TEAMMATES; i++)
        {
            VolleyballAgent a = (i < teammates.Count) ? teammates[i] : null;
            if (a != null)
            {
                Vector3 toMate = new Vector3(
                    (a.transform.position.x - transform.position.x) * agentRot,
                    (a.transform.position.y - transform.position.y),
                    (a.transform.position.z - transform.position.z) * agentRot
                );
                float dist = Mathf.Clamp01(toMate.magnitude / maxDist);

                Vector3 mateDir = toMate.sqrMagnitude > 1e-6f ? toMate.normalized : Vector3.zero;
                AddV(mateDir); // 3

                AddF(dist); // 1

                Vector3 mateVel = a.GetComponent<Rigidbody>().linearVelocity / 10f;
                AddF(mateVel.y);
                AddF(mateVel.z * agentRot);
                AddF(mateVel.x * agentRot);
                AddF(a.role == Role.Passer ? 1f : 0f);
                AddF(a.role == Role.Hitter ? 1f : 0f);
                AddF(a.role == Role.Setter ? 1f : 0f);
                AddF(a.role == Role.Generic ? 1f : 0f);
            }
            else
            {
                AddV(Vector3.zero); // 3
                AddF(0f);           // 1
                AddF(0f);           // vy
                AddF(0f);           // vz
                AddF(0f);           // vx
                AddF(0f);           // passer
                AddF(0f);           // hitter
                AddF(0f);           // setter
                AddF(0f);           // generic
            }
        }

        for (int i = 0; i < MAX_OPPONENTS; i++)
        {
            VolleyballAgent a = (i < opponents.Count) ? opponents[i] : null;
            if (a != null)
            {
                Vector3 toMate = new Vector3(
                    (a.transform.position.x - transform.position.x) * agentRot,
                    (a.transform.position.y - transform.position.y),
                    (a.transform.position.z - transform.position.z) * agentRot
                );

                Vector3 oppDir = toMate.sqrMagnitude > 1e-6f ? toMate.normalized : Vector3.zero;
                AddV(oppDir); // 3

                Vector3 oppVel = a.GetComponent<Rigidbody>().linearVelocity / 10f;
                AddF(oppVel.y);
                AddF(oppVel.z * agentRot);
                AddF(oppVel.x * agentRot);
                AddF(a.role == Role.Passer ? 1f : 0f);
                AddF(a.role == Role.Hitter ? 1f : 0f);
                AddF(a.role == Role.Setter ? 1f : 0f);
                AddF(a.role == Role.Generic ? 1f : 0f);
            }
            else
            {
                AddV(Vector3.zero); // 3
                AddF(0f);           // 1
                AddF(0f);           // vy
                AddF(0f);           // vz
                AddF(0f);           // vx
                AddF(0f);           // passer
                AddF(0f);           // hitter
                AddF(0f);           // setter
                AddF(0f);           // generic
            }
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
        AddF(role == Role.Setter ? 1f : 0f);
        AddF(role == Role.Generic ? 1f : 0f);

        float lastTouch = (envController.lastHitterAgent == this) ? 1f :
            (teammates.Contains(envController.lastHitterAgent)) ? -1f : 0f;
        AddF(lastTouch);

        for (int i = 0; i < MAX_AGENTS; i++)
        {
            VolleyballAgent a = (i < envController.AgentsList.Count) ? envController.AgentsList[i] : null;
            if (a != null)
            {
                AddF(a.isSpiking ? 1f : 0f);
                AddF(a.isSetting ? 1f : 0f);
            }
            else
            {
                AddF(0f);
                AddF(0f);
            }
        }

        AddF((float)teammates.Count / MAX_TEAMMATES);   // Normalized teammate count
        AddF((float)opponents.Count / MAX_OPPONENTS);   // Normalized opponent count

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

        if (k.pKey.isPressed) da[3] = 2;   // spike

        if (k.bKey.isPressed) da[4] = 1;      // set
    }
}
