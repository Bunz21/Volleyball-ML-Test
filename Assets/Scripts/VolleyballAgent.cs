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

    public enum Position
    {
        Front,
        Back,
        Generic
    }

    [HideInInspector]
    public Rigidbody agentRb;

    public Position position;

    [HideInInspector]
    public Vector3 initialPos;
    public float rotSign;

    private float m_Existential;
    private float m_LateralSpeed;
    private float m_ForwardSpeed;
    private float m_JumpForce;

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

        // Create spike collider
        spikeCollider = gameObject.AddComponent<BoxCollider>();
        spikeCollider.size = new Vector3(1f, 2f, 0.2f); // adjust to fit hand/arm
        spikeCollider.center = new Vector3(0f, 1.5f, 0.5f); // forward and up
        spikeCollider.isTrigger = true;
        spikeCollider.enabled = false;

        // Create block collider
        blockCollider = gameObject.AddComponent<BoxCollider>();
        blockCollider.size = new Vector3(1f, 2f, 0.2f); // adjust to fit arms
        blockCollider.center = new Vector3(0f, 1f, 0f); // raised above agent center
        blockCollider.isTrigger = true;
        blockCollider.enabled = false;

        var envController = GetComponentInParent<EnvironmentController>();
        if (envController != null)
        {
            m_Existential = 1f / envController.MaxEnvironmentSteps;
        }
        else
        {
            m_Existential = 1f / MaxStep;
        }

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

        // Assign speed and jump based on position
        switch (position)
        {
            case Position.Front:
                m_LateralSpeed = 1f;
                m_ForwardSpeed = 1f;
                m_JumpForce = 5f;
                break;
            case Position.Back:
                m_LateralSpeed = 1f;
                m_ForwardSpeed = 0.7f;
                m_JumpForce = 4f;
                break;
            default:
                m_LateralSpeed = 1f;
                m_ForwardSpeed = 1f;
                m_JumpForce = 4.5f;
                break;
        }
    }


    public override void OnEpisodeBegin()
    {
        m_BallTouch = m_ResetParams.GetWithDefault("ball_touch", 0);
        spikeActive = false;
        blockActive = false;
        spikeCollider.enabled = false;
        blockCollider.enabled = false;
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
        var ballController = FindFirstObjectByType<VolleyballBallController>();
        if (ballController != null && ballController.InMiniServe)
        {
            allowSpecial = false;
        }

        // Actions mapping
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

        if (jump && IsGrounded())
        {
            agentRb.AddForce(Vector3.up * m_JumpForce, ForceMode.VelocityChange);
        }

        // Activate colliders if spike/block
        if (spikeActive || blockActive)
        {
            spikeCollider.enabled = spikeActive;
            blockCollider.enabled = blockActive;
            actionTimer = actionDuration;
        }

        // Countdown for collider duration
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
