using UnityEngine;

public class VolleyballSettings : MonoBehaviour
{
    [Header("Agent Settings")]
    public float agentRunSpeed = 1.25f;
    public float agentJumpHeight = 3.5f;
    public float agentJumpVelocity = 600f;
    public float agentJumpVelocityMaxChange = 10f;
    public float spikePower = 15; // TEST
    public float bumpPower = 15f;   // TEST

    // Slows down strafe & backward movement
    public float speedReductionFactor = 0.75f;

    [Header("Team Materials")]
    public Material blueGoalMaterial;
    public Material redGoalMaterial;
    public Material defaultMaterial;

    // This is a downward force applied when falling to make jumps look less floaty
    public float fallingForce = 50;
}