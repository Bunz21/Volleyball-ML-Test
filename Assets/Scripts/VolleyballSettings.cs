using UnityEngine;

public class VolleyballSettings : MonoBehaviour
{
    [Header("Agent Settings")]
    public float agentRunSpeed = 1.5f;
    public float agentJumpHeight = 3.25f;
    public float agentJumpVelocity = 777f;
    public float agentJumpVelocityMaxChange = 10f;
    public float spikePower = 30; // TEST
    public float bumpPower = 10f;   // TEST

    // Slows down strafe & backward movement
    public float speedReductionFactor = 0.75f;

    [Header("Team Materials")]
    public Material blueGoalMaterial;
    public Material redGoalMaterial;
    public Material defaultMaterial;

    // This is a downward force applied when falling to make jumps look less floaty
    public float fallingForce = 25;
}