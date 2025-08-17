using UnityEngine;

public class VolleyballController : MonoBehaviour
{
    [HideInInspector]
    public EnvironmentController envController;

    public GameObject redFloor;
    public GameObject blueFloor;
    Collider redFloorCollider;
    Collider blueFloorCollider;

    void Start()
    {
        envController = GetComponentInParent<EnvironmentController>();
        redFloorCollider = redFloor.GetComponent<Collider>();
        blueFloorCollider = blueFloor.GetComponent<Collider>();
    }

    /// <summary>
    /// Detects whether the ball lands in the blue, red, or out of bounds area
    /// </summary>
    void OnTriggerEnter(Collider other)
    {
        if (other.gameObject.CompareTag("outofbounds") || other.gameObject.CompareTag("antenna") || other.gameObject.CompareTag("ofbdetector"))
        {
            // ball went out of bounds
            envController.ResolveEvent(Event.HitOutOfBounds);
        }
        else if (other.gameObject.CompareTag("floorRed"))
        {
            // ball hit into red side
            envController.ResolveEvent(Event.HitRedGoal);
        }
        else if (other.gameObject.CompareTag("floorBlue"))
        {
            // ball hit into blue side
            envController.ResolveEvent(Event.HitBlueGoal);
        }
        else if (other.gameObject.CompareTag("overnetdetector"))
        {
            // ball hit over net
            envController.ResolveEvent(Event.PassOverNet);
        }
        else if (other.gameObject.CompareTag("blueAgent") || other.gameObject.CompareTag("redAgent"))
        {
            // ball hit over net
            envController.ResolveEvent(Event.AgentTouch);
        }
    }
}