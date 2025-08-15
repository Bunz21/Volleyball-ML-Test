using UnityEngine;

public class VolleyballSettings : MonoBehaviour
{
    [Header("Team Materials")]
    public Material redMaterial;
    public Material blueMaterial;

    [Header("Training Options")]
    public bool randomizePlayersTeamForTraining = true;

    [Header("Agent Movement")]
    public float agentRunSpeed = 5f;

    [Header("Volleyball-specific Settings")]
    public float agentJumpForce = 5f;  // Default jump force
    public float spikePower = 8f;       // Force applied when spiking
    public float blockPower = 2f;       // Force applied when spiking
    public float blockHeight = 2f;      // Height of block collider
}
