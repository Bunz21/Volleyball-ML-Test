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
        //----------------------------------------------------------
        // NEW: first legal touch while the ball is still a trigger
        //----------------------------------------------------------
        VolleyballAgent agent = other.GetComponent<VolleyballAgent>();
        if (agent != null)
        {
            envController.D("COLLIDE " + agent.teamId + " AGENT");
            envController.RegisterTouch(agent);   // will un-freeze the ball
            return;                               // nothing else to do
        }

        //----------------------------------------------------------
        // existing floor / antenna / over-net logic
        //----------------------------------------------------------
        if (other.CompareTag("outofbounds") || other.CompareTag("antenna") ||
            other.CompareTag("ofbdetector"))
        {
            envController.ResolveEvent(Event.HitOutOfBounds);
            envController.D("OUTOFBOUNDS");
        }
        else if (other.CompareTag("floorRed"))
        {
            envController.ResolveEvent(Event.HitRedGoal);
            envController.D("COLLIDE RED GOAL");
        }
        else if (other.CompareTag("floorBlue"))
        {
            envController.ResolveEvent(Event.HitBlueGoal);
            envController.D("COLLIDE BLUE GOAL");
        }
        else if (other.CompareTag("overnetdetector"))
        {
            envController.ResolveEvent(Event.PassOverNet);
            envController.D("COLLIDE OVER NET");
        }
    }
}