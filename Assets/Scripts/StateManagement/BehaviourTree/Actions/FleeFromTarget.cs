using UnityEngine;

public class FleeFromTarget : ActionNode
{
    private Transform target;
    private float speed;
    private float fleeDistance;

    public FleeFromTarget(Transform transform, Transform target, float speed, float fleeDistance) : base(transform)
    {
        this.target = target;
        this.speed = speed;
        this.fleeDistance = fleeDistance;
    }

    protected override NodeState DoEvaluate()
    {
        if(target == null)
        {
            state = NodeState.Failure;
            return state;
        }

        float distance = Vector3.Distance(transform.position, target.position);

        if(distance >= fleeDistance)
        {
            state = NodeState.Success;
            return state;
        }

        Vector3 fleeDirection = (transform.position - target.position).normalized;
        transform.position += fleeDirection * speed * Time.deltaTime;
        transform.LookAt(transform.position + fleeDirection);

        state = NodeState.Running;
        return state;
    }
    
    
}