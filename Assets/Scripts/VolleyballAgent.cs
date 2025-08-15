using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

public enum Team
{
    Blue = 0,
    Red = 1
}

public class VolleyballAgent : Agent
{

    [HideInInspector]
    public Team team;

    [HideInInspector]
    public Rigidbody agentRb;

    [HideInInspector]
    public Vector3 initialPos;
    public float rotSign;

    private float m_Existential;
    private float m_LateralSpeed;
    private float m_ForwardSpeed;
    private float m_BaseJumpForce; // store base jump force for scaling

    private float m_BallTouch;

    private float actionDuration = 0.2f; // seconds
    private float actionTimer = 0f;

    EnvironmentParameters m_ResetParams;
    VolleyballSettings m_Settings;

    // Colliders for spike/block
    public BoxCollider spikeCollider;
    public BoxCollider blockCollider;
    private bool spikeActive;
    private bool blockActive;


    public override void Initialize()
    {
        agentRb = GetComponent<Rigidbody>();
        agentRb.maxAngularVelocity = 500;

        // Spike collider
        spikeCollider = gameObject.AddComponent<BoxCollider>();
        spikeCollider.size = new Vector3(0.5f, 1f, 0.2f);
        spikeCollider.center = new Vector3(0f, 1.5f, 0.5f);
        spikeCollider.isTrigger = true;
        spikeCollider.enabled = false;

        // Block collider
        blockCollider = gameObject.AddComponent<BoxCollider>();
        blockCollider.size = new Vector3(0.3f, 1f, 0.2f);
        blockCollider.center = new Vector3(0f, 1f, 0f);
        blockCollider.isTrigger = true;
        blockCollider.enabled = false;

        var envController = GetComponentInParent<EnvironmentController>();
        m_Existential = envController != null ? 1f / envController.MaxEnvironmentSteps : 1f / MaxStep;

        m_Settings = FindFirstObjectByType<VolleyballSettings>();
        m_ResetParams = Academy.Instance.EnvironmentParameters;

        if (GetComponent<BehaviorParameters>().TeamId == (int)Team.Blue)
        {
            team = Team.Blue;
            initialPos = transform.position + new Vector3(-5f, 0.5f, 0);
            rotSign = 1f;
        }
        else
        {
            team = Team.Red;
            initialPos = transform.position + new Vector3(5f, 0.5f, 0);
            rotSign = -1f;
        }

        // Set default movement speeds (no role-based changes)
        m_LateralSpeed = 1f;
        m_ForwardSpeed = 1f;
        m_BaseJumpForce = m_Settings.agentJumpForce; // store a base value
    }


    public override void OnEpisodeBegin()
    {
        m_BallTouch = m_ResetParams.GetWithDefault("ball_touch", 0);
        spikeActive = false;
        blockActive = false;
        spikeCollider.enabled = false;
        blockCollider.enabled = false;
    }

    protected new void Awake()
    {
        base.Awake();

        m_ResetParams = Academy.Instance.EnvironmentParameters;

        // Ensure colliders exist
        if (spikeCollider == null)
        {
            spikeCollider = gameObject.AddComponent<BoxCollider>();
            spikeCollider.size = new Vector3(0.5f, 1f, 0.2f);
            spikeCollider.center = new Vector3(0f, 1.5f, 0.5f);
            spikeCollider.isTrigger = true;
            spikeCollider.enabled = false;
        }

        if (blockCollider == null)
        {
            blockCollider = gameObject.AddComponent<BoxCollider>();
            blockCollider.size = new Vector3(0.3f, 1f, 0.2f);
            blockCollider.center = new Vector3(0f, 1f, 0f);
            blockCollider.isTrigger = true;
            blockCollider.enabled = false;
        }
    }




    public override void OnActionReceived(ActionBuffers actions)
    {
        var discreteActions = actions.DiscreteActions;
        MoveAgent(discreteActions);

        // Existential reward
        AddReward(m_Existential);
    }

    private void MoveAgent(ActionSegment<int> act)
    {
        Vector3 dirToGo = Vector3.zero;
        Vector3 rotateDir = Vector3.zero;

        spikeActive = false;
        blockActive = false;
        bool jump = false;

        // Don't allow spike/block if ball is in mini-serve
        bool allowSpecial = true;
        var ballController = FindFirstObjectByType<VolleyballController>();
        if (ballController != null && ballController.InMiniServe)
            allowSpecial = false;

        // Movement actions
        switch (act[0])
        {
            case 1: dirToGo += transform.forward * m_ForwardSpeed; break;
            case 2: dirToGo += -transform.forward * m_ForwardSpeed; break;
        }

        switch (act[1])
        {
            case 1: dirToGo += transform.right * m_LateralSpeed; break;
            case 2: dirToGo += -transform.right * m_LateralSpeed; break;
        }

        // Rotation
        switch (act[2])
        {
            case 1: rotateDir = -transform.up; break;
            case 2: rotateDir = transform.up; break;
        }

        // Jump / Spike / Block
        switch (act[3])
        {
            case 1: jump = true; break;
            case 2: if (allowSpecial) spikeActive = true; break;
            case 3: if (allowSpecial) blockActive = true; break;
        }

        // Apply rotation and movement
        transform.Rotate(rotateDir, Time.deltaTime * 100f);
        agentRb.AddForce(dirToGo * m_Settings.agentRunSpeed, ForceMode.VelocityChange);

        // Jump scales with current horizontal speed
        if (jump && IsGrounded())
        {
            float speedMultiplier = new Vector3(agentRb.linearVelocity.x, 0, agentRb.linearVelocity.z).magnitude;
            float jumpForce = m_BaseJumpForce + speedMultiplier; // additive scaling
            agentRb.AddForce(Vector3.up * jumpForce, ForceMode.VelocityChange);
        }

        // Activate spike/block colliders
        if (spikeActive || blockActive)
        {
            spikeCollider.enabled = spikeActive;
            blockCollider.enabled = blockActive;
            actionTimer = actionDuration;

            // Optional: scale spike power by horizontal speed
            if (spikeActive)
            {
                float spikePower = m_Settings.spikePower * (1f + new Vector3(agentRb.linearVelocity.x, 0, agentRb.linearVelocity.z).magnitude);
                // Apply force to ball elsewhere in spike logic
            }
        }

        // Countdown collider duration
        if (actionTimer > 0f)
        {
            actionTimer -= Time.deltaTime;
            if (actionTimer <= 0f)
            {
                spikeCollider.enabled = false;
                blockCollider.enabled = false;
            }
        }
    }


    private bool IsGrounded()
    {
        return Physics.Raycast(transform.position, Vector3.down, 0.55f);
    }

    public void StartBlock()
    {
        blockCollider.enabled = true;
    }

    public void EndBlock()
    {
        blockCollider.enabled = false;
    }


    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("ball"))
        {
            Rigidbody ballRb = other.attachedRigidbody;

            if (spikeActive)
            {
                Vector3 spikeDir = (Vector3.up + transform.forward).normalized;
                ballRb.AddForce(spikeDir * m_Settings.spikePower, ForceMode.VelocityChange);
                AddReward(0.3f * m_BallTouch);
            }

            if (blockActive)
            {
                Vector3 blockDir = (Vector3.up + -transform.forward).normalized;
                ballRb.AddForce(blockDir * m_Settings.blockPower, ForceMode.VelocityChange);
                AddReward(0.2f * m_BallTouch);
            }
        }
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var discreteActionsOut = actionsOut.DiscreteActions;

        // Movement
        if (Input.GetKey(KeyCode.W)) discreteActionsOut[0] = 1;
        if (Input.GetKey(KeyCode.S)) discreteActionsOut[0] = 2;
        if (Input.GetKey(KeyCode.E)) discreteActionsOut[1] = 1;
        if (Input.GetKey(KeyCode.Q)) discreteActionsOut[1] = 2;
        if (Input.GetKey(KeyCode.A)) discreteActionsOut[2] = 1;
        if (Input.GetKey(KeyCode.D)) discreteActionsOut[2] = 2;

        // Jump / Spike / Block
        if (Input.GetKey(KeyCode.Space)) discreteActionsOut[3] = 1; // Jump
        if (Input.GetKey(KeyCode.LeftShift)) discreteActionsOut[3] = 2; // Spike
        if (Input.GetKey(KeyCode.LeftControl)) discreteActionsOut[3] = 3; // Block
    }

    private void Update()
    {
        if (actionTimer > 0f)
        {
            actionTimer -= Time.deltaTime;
            if (actionTimer <= 0f)
            {
                spikeCollider.enabled = false;
                blockCollider.enabled = false;
            }
        }
    }
}
