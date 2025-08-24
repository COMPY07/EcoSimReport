using System.Collections.Generic;
using UnityEngine;

public class MoveActionNode : ActionNode {
        protected Transform target;
        protected float moveSpeed;
        protected float stoppingDistance;
        protected List<Vector3> currentPath;
        protected int currentWaypointIndex;
        protected float pathRequestCooldown;
        protected float lastPathRequestTime;
        protected int agentId;
        protected Vector3 smoothedDirection;
        protected float turnSpeed = 10f;


        protected bool useRotate = false;
        public MoveActionNode(Transform transform, Transform target, float moveSpeed = 5f) 
            : base(transform) {
            this.target = target;
            this.moveSpeed = moveSpeed;
            this.stoppingDistance = .5f;
            this.agentId = transform.GetInstanceID();
            pathRequestCooldown = 10f; // 이거 경로를 분할해서 받아가지고 하나씩 받을 때 이거 중간에 넘겨주니까 이상하게 가는건데 이거는 금방 해결할 듯?
            
            // Debug.Log(agentId);
        }

        
        protected override NodeState DoEvaluate() {
            // if (target == null) return NodeState.Failure;
            //
            // float distanceToTarget = Vector3.Distance(transform.position, target.position);
            //
            // if (distanceToTarget <= stoppingDistance) {
            //     StopMoving();
            //     return NodeState.Success;
            // }
            //
            // if (Time.time - lastPathRequestTime > pathRequestCooldown) RequestNewPath();
            //
            //
            // // if (currentPath != null && currentPath.Count > 0)
            // //     return NodeState.Running;

            

            return NodeState.Running;
        }
        public override void FixedEvaluate()
        {
            // if (currentPath != null && currentPath.Count > 0) FollowPath();
        }

        protected void RequestNewPath() {
            lastPathRequestTime = Time.time;
            AStarManager.Instance.RequestPath(
                transform.position,
                target.position,
                agentId,
                OnPathReceived
            );
        }

        protected void OnPathReceived(List<Vector3> path)
        {
            currentPath = path;
            currentWaypointIndex = 0;
        }

        protected void FollowPath()
        {
            if (currentWaypointIndex >= currentPath.Count) {
                currentPath = null;
                return;
            }
            
            Vector3 targetWaypoint = currentPath[currentWaypointIndex];
            targetWaypoint.z = transform.position.z; 
            Vector3 direction = (targetWaypoint - transform.position).normalized;
            
            animator.SetFloat("moveX", direction.x);
            animator.SetFloat("moveY", direction.y);
            
            smoothedDirection = Vector3.Slerp(smoothedDirection, direction, Time.deltaTime * turnSpeed);
            
            rigidbody.linearVelocity = new Vector2(smoothedDirection.x, smoothedDirection.y) * moveSpeed;
            
            
            if (useRotate && smoothedDirection != Vector3.zero) 
            {
                float angle = Mathf.Atan2(smoothedDirection.y, smoothedDirection.x) * Mathf.Rad2Deg - 90f;
                transform.rotation = Quaternion.AngleAxis(angle, Vector3.forward);
            }
            
            if (Vector3.Distance(transform.position, targetWaypoint) < 0.45f) currentWaypointIndex++;
            if (animator != null)
            {
                float velocity = rigidbody.linearVelocity.magnitude;
                animator.SetFloat("Speed", velocity);
                animator.SetBool("Moving", true);
            }
        }

        protected void StopMoving()
        {
            rigidbody.linearVelocity = Vector3.zero;
            if(currentPath != null)
                currentPath.Clear();
            
            animator.SetBool("Moving", false);
            
            if (animator != null) animator.SetFloat("Speed", 0);
            
        }


        public void SetTarget(Transform transform)
        {
            this.target = transform;
        }
    
}