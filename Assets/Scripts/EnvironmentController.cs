using System.Collections.Generic;
using Unity.MLAgents;
using UnityEngine;

public class EnvironmentController : MonoBehaviour
{
    [System.Serializable]
    public class PlayerInfo
    {
        public VolleyballAgent Agent;
        [HideInInspector] public Vector3 StartingPos;
        [HideInInspector] public Quaternion StartingRot;
        [HideInInspector] public Rigidbody Rb;
    }

    [Header("Environment Settings")]
    [Tooltip("Max Academy steps before environment resets")]
    public int MaxEnvironmentSteps = 25000;

    [Header("References")]
    public GameObject ball;
    [HideInInspector] public Rigidbody ballRb;
    public List<PlayerInfo> AgentsList = new List<PlayerInfo>();

    private VolleyballController ballController;

    private SimpleMultiAgentGroup m_BlueAgentGroup;
    private SimpleMultiAgentGroup m_RedAgentGroup;

    private int m_ResetTimer;

    void Start()
    {
        ballRb = ball.GetComponent<Rigidbody>();
        ballController = ball.GetComponent<VolleyballController>();

        m_BlueAgentGroup = new SimpleMultiAgentGroup();
        m_RedAgentGroup = new SimpleMultiAgentGroup();

        foreach (var item in AgentsList)
        {
            item.StartingPos = item.Agent.transform.position;
            item.StartingRot = item.Agent.transform.rotation;
            item.Rb = item.Agent.GetComponent<Rigidbody>();

            if (item.Agent.team == Team.Blue)
                m_BlueAgentGroup.RegisterAgent(item.Agent);
            else
                m_RedAgentGroup.RegisterAgent(item.Agent);
        }

        ResetScene();
    }

    void FixedUpdate()
    {
        m_ResetTimer++;
        if (MaxEnvironmentSteps > 0 && m_ResetTimer >= MaxEnvironmentSteps)
        {
            m_BlueAgentGroup.GroupEpisodeInterrupted();
            m_RedAgentGroup.GroupEpisodeInterrupted();
            ResetScene();
        }
    }

    /// <summary>
    /// Called by BallController when a point is scored
    /// </summary>
    public void PointScored(Team scoringTeam)
    {
        float stepPenalty = (float)m_ResetTimer / MaxEnvironmentSteps;

        if (scoringTeam == Team.Blue)
        {
            m_BlueAgentGroup.AddGroupReward(1f - stepPenalty);
            m_RedAgentGroup.AddGroupReward(-1f);
        }
        else
        {
            m_RedAgentGroup.AddGroupReward(1f - stepPenalty);
            m_BlueAgentGroup.AddGroupReward(-1f);
        }

        // Reset ball for mini-serve
        ballController.ResetTouches();
        ballController.ResetBallMiniServe(scoringTeam);
    }

    /// <summary>
    /// Reset all agents and ball positions without ending episode
    /// </summary>
    public void ResetScene()
    {
        m_ResetTimer = 0;

        foreach (var item in AgentsList)
        {
            var randomOffsetX = Random.Range(-2f, 2f);
            var newPos = item.StartingPos + new Vector3(randomOffsetX, 0f, 0f);
            item.Agent.transform.SetPositionAndRotation(newPos, item.StartingRot);

            item.Rb.linearVelocity = Vector3.zero;
            item.Rb.angularVelocity = Vector3.zero;
        }

        ResetBall();
    }

    private void ResetBall()
    {
        ball.transform.position = Vector3.up * 0.5f; // center slightly above floor
        ballRb.linearVelocity = Vector3.zero;
        ballRb.angularVelocity = Vector3.zero;
    }
}
