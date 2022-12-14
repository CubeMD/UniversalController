using System.Collections.Generic;
using System.Linq;
using AgentDebugTool.Scripts.Agent;
using Mono.Cecil;
using Systems.Interfaces;
using Systems.Orders;
using Templates;
using Tools;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

#if UNITY_EDITOR
using UnityEditor;
#endif

using UnityEngine;
using Cursor = UnityEngine.Cursor;
using Random = UnityEngine.Random;

public class RtsAgent : DebuggableAgent
{
    public class RtsAgentAction
    {
        public Vector2 currentCursorAction;
        public float timeForScheduledDecision;
        public bool currentShiftAction;
        public List<Unit> selectedUnits;

        public RtsAgentAction(Vector2 currentCursorAction, float timeForScheduledDecision, bool currentShiftAction, List<Unit> selectedUnits)
        {
            this.currentCursorAction = currentCursorAction;
            this.timeForScheduledDecision = timeForScheduledDecision;
            this.currentShiftAction = currentShiftAction;
            this.selectedUnits = selectedUnits;
        }

        public RtsAgentAction()
        {
            
        }
    }
    
    public class RtsAgentObservation
    {
        public float[] vectorObservations;
        public List<List<float[]>> observations;
            
        public RtsAgentObservation(float[] vectorObservations, List<List<float[]>> observations)
        {
            this.vectorObservations = vectorObservations;
            this.observations = observations;
        }
    }

    private class AgentStep
    {
        public RtsAgentAction rtsAgentAction;
        public RtsAgentObservation rtsAgentObservation;
        public float reward;
    }
    
    [Header("Systems")]
    [SerializeField] private BufferSensorComponent reclaimSensorComponent;
    [SerializeField] private BufferSensorComponent unitSensorComponent;
    [SerializeField] private BufferSensorComponent orderSensorComponent;
    [SerializeField] public Environment environment;
    [SerializeField] public Order orderPrefab;
    [SerializeField] private Camera cam;
    
    [Header("Transforms")]
    [SerializeField] private Transform cameraTransform;
    [SerializeField] private Transform cameraGizmoTransform;
    [SerializeField] private Transform cursorTransform;
    [SerializeField] private Transform selectionBoxTransform;
    
    [Header("Unit")]
    [SerializeField] private Unit unitPrefab;
    [SerializeField] private UnitTemplate startingUnitTemplate;
    [SerializeField] private int numStartingUnits = 1;
    [SerializeField] private float startingUnitSpread = 20;
    [SerializeField] private float timeBetweenUnitSpawns;
    [SerializeField] private float maxNumUnits;
    [SerializeField] private int numOrderObservationsPerUnit;
    
    [Header("Parameters")]
    [SerializeField] private float zoomSpeed;
    [SerializeField] private Vector2 zoomMinMax;
    [SerializeField] private List<float> possibleDelays;
    [SerializeField] private LayerMask interactableLayerMask;

    [Header("Extra")]
    [SerializeField] private bool isHuman;
    [SerializeField] private bool drawBufferSensorMonitor;
    [SerializeField] private bool detailedDebug;
    
    private float CameraZoom
    {
        get => cam.orthographicSize;
        set
        {
            cam.orthographicSize = value;
            Vector3 gizmoScale = Vector3.one * (value * 2);
            gizmoScale.y = 1;
            cameraGizmoTransform.localScale = gizmoScale;
        }
    }

    private float MapSize => environment.halfGroundSize;

    private readonly List<Unit> ownedUnits = new List<Unit>();
    private readonly List<Unit> selectedUnits = new List<Unit>();
    private readonly List<AgentStep> episodeTrajectory = new List<AgentStep>();
    
    private float timeSinceUnitSpawned;
    //private bool humanIsHoldingLeftMouseButton;
    private AgentStep completedRtsAgentStep; // if the agent is not controlled by human it will contain only the last decided action
    private float timeSinceLastDecision;


    public override void Initialize()
    {
        DetailedDebug($"Init at time {Time.time}");

        if (isHuman)
        {
            Cursor.lockState = CursorLockMode.Confined;
        }
    }
    
    // public void Start()
    // {
    //     DetailedDebug($"Start at time {Time.time}");
    //     Academy.Instance.EnvironmentStep();
    // }

    public override void OnEpisodeBegin()
    {
        DetailedDebug($"Episode begin at time {Time.time}");
        
        CameraZoom = zoomMinMax.x;
        cameraTransform.localPosition = Vector3.zero;
        cursorTransform.localPosition = Vector3.zero;
        selectionBoxTransform.localScale = Vector3.zero;
        selectionBoxTransform.localPosition = Vector3.zero;
        
        ownedUnits.Clear();
        selectedUnits.Clear();

        completedRtsAgentStep = new AgentStep();
        timeSinceLastDecision = 0;
        timeSinceUnitSpawned = 0;

        if (isHuman)
        {
            //humanIsHoldingLeftMouseButton = false;
            episodeTrajectory.Clear();
        }
        
        SpawnStartingUnits();
    }
    
    private void SpawnStartingUnits()
    {
        for (int i = 0; i < numStartingUnits; i++)
        {
            Vector3 localPosition = new Vector3(
                Random.Range(-startingUnitSpread, startingUnitSpread), 
                0, 
                Random.Range(-startingUnitSpread, startingUnitSpread));

            SpawnUnit(startingUnitTemplate, localPosition);
        }
    }

    private void SpawnUnit(UnitTemplate unitTemplate, Vector3 localPosition)
    {
        Unit unit = ObjectPooler.InstantiateGameObject(unitPrefab, transform.parent.localPosition + localPosition, Quaternion.identity, transform.parent);
        unit.SetUnitTemplate(unitTemplate, this);
        unit.OnDestroyableDestroy += HandleUnitDestroyed;
        ownedUnits.Add(unit);
    }
    
    public void FixedUpdate()
    {
        DetailedDebug($"Fixed update at time {Time.time}");

        timeSinceUnitSpawned += Time.fixedDeltaTime;

        if (timeSinceUnitSpawned >= timeBetweenUnitSpawns && ownedUnits.Count < maxNumUnits)
        {
            SpawnStartingUnits();
            timeSinceUnitSpawned = 0;
        }
        
        if (isHuman)
        {
            // if (timeSinceLastDecision == 0)
            // {
            //     completedRtsAgentStep.rtsAgentObservation = GetAgentEnvironmentObservation();
            // }
            //
            // timeSinceLastDecision += Time.fixedDeltaTime;
            //
            // if (timeSinceLastDecision >= delayMax || completedRtsAgentStep.rtsAgentAction != null)
            // {
            //     DetailedDebug($"Action recorded in fixed update at time {Time.time}");
            //     
            //     Vector3 humanCursorPositionRelativeToCamera = cameraTransform.InverseTransformPoint(cam.ScreenToWorldPoint(Input.mousePosition));
            //     Vector2 cursorAction = new Vector2(
            //         Mathf.Clamp(humanCursorPositionRelativeToCamera.x / CameraZoom, -1f, 1), 
            //         Mathf.Clamp(humanCursorPositionRelativeToCamera.z / CameraZoom, -1f, 1));
            //     
            //     completedRtsAgentStep.rtsAgentAction ??= new RtsAgentAction(cursorAction, timeSinceLastDecision, AgentActionType.None, false, 0);
            //     
            //     // // Regularize continuous actions
            //     // AddReward(-0.005f);
            //     // AddReward(-0.005f * completedRtsAgentStep.rtsAgentAction.currentCursorAction.magnitude);
            //     // AddReward(completedRtsAgentStep.rtsAgentAction.currentZoomAction != 0 ? -0.01f : 0f);
            //     
            //     completedRtsAgentStep.reward = GetCumulativeReward();
            //     episodeTrajectory.Add(completedRtsAgentStep);
            //     
            //     InteractWithEnvironment(completedRtsAgentStep.rtsAgentAction);
            //     
            //     timeSinceLastDecision = 0;
            //     completedRtsAgentStep = new AgentStep();
            // }
        }
        else if (!isHuman)
        {
            bool hasCompletedAction = completedRtsAgentStep.rtsAgentAction != null;
            
            timeSinceLastDecision += Time.fixedDeltaTime;
            
            if (!hasCompletedAction || timeSinceLastDecision >= completedRtsAgentStep.rtsAgentAction.timeForScheduledDecision)
            {
                // if (hasCompletedAction)
                // {
                //     
                // }
                
                timeSinceLastDecision = 0;
                RequestDecision();
                // Academy.Instance.EnvironmentStep();
            }
        }
    }
    
    private void Update()
    {
        // DetailedDebug($"Update at time {Time.time}");
        //
        // if (!isHuman || 
        //     !Application.isFocused ||
        //     IsMouseOutOfScreen() || 
        //     completedRtsAgentStep.rtsAgentAction != null) return;
        //
        // if (Input.mouseScrollDelta.y != 0 && !humanIsHoldingLeftMouseButton)
        // {
        //     completedRtsAgentStep.rtsAgentAction ??= new RtsAgentAction
        //     {
        //         currentZoomAction = -Input.mouseScrollDelta.y < 0 ? -1 : 1
        //     };
        // }
        // else if (Input.GetKeyDown(KeyCode.Mouse0))
        // {
        //     humanIsHoldingLeftMouseButton = true;
        //     
        //     completedRtsAgentStep.rtsAgentAction ??= new RtsAgentAction
        //     {
        //         currentAgentActionType = AgentActionType.None
        //     };
        // }
        // else if (Input.GetKeyUp(KeyCode.Mouse0))
        // {
        //     humanIsHoldingLeftMouseButton = false;
        //     
        //     completedRtsAgentStep.rtsAgentAction ??= new RtsAgentAction
        //     {
        //         currentAgentActionType = AgentActionType.LeftDrag
        //     };
        // }
        // else if (Input.GetKeyDown(KeyCode.Mouse1) && !humanIsHoldingLeftMouseButton)
        // {
        //     completedRtsAgentStep.rtsAgentAction ??= new RtsAgentAction
        //     {
        //         currentAgentActionType = AgentActionType.RightClick
        //     };
        // }
        //
        // if (completedRtsAgentStep.rtsAgentAction != null)
        // {
        //     DetailedDebug($"Action recorded in update at time {Time.time}");
        //     Vector3 humanCursorPositionRelativeToCamera = cameraTransform.InverseTransformPoint(cam.ScreenToWorldPoint(Input.mousePosition));
        //     
        //     completedRtsAgentStep.rtsAgentAction.currentCursorAction = new Vector2(
        //         Mathf.Clamp(humanCursorPositionRelativeToCamera.x / CameraZoom, -1f, 1), 
        //         Mathf.Clamp(humanCursorPositionRelativeToCamera.z / CameraZoom, -1f, 1));
        //     
        //     completedRtsAgentStep.rtsAgentAction.timeForScheduledDecision = timeSinceLastDecision;
        //     
        //     if (Input.GetKey(KeyCode.LeftShift))
        //     {
        //         completedRtsAgentStep.rtsAgentAction.currentShiftAction = true;
        //     }
        // }
    }

    private bool IsMouseOutOfScreen()
    {
        return Input.mousePosition.x < 0 ||
               Input.mousePosition.x > 1000 ||
               Input.mousePosition.y < 0 ||
               Input.mousePosition.y > 1000;
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        // if (isHuman)
        // {
        //     ActionSegment<float> continuousActions = actionsOut.ContinuousActions;
        //     ActionSegment<int> discreteActions = actionsOut.DiscreteActions;
        //     
        //     RtsAgentAction nextAction = episodeTrajectory[0].rtsAgentAction;
        //
        //     continuousActions[0] = nextAction.currentCursorAction.x;
        //     continuousActions[1] = nextAction.currentCursorAction.y;
        //     
        //     discreteActions[0] = (int)nextAction.currentAgentActionType;
        //     discreteActions[1] = nextAction.currentShiftAction ? 1 : 0;
        //     discreteActions[2] = nextAction.currentZoomAction;
        //     discreteActions[3] = possibleDelays.IndexOf(possibleDelays.First(x => nextAction.timeForScheduledDecision <= x));
        //     
        //     SetReward(episodeTrajectory[0].reward);
        // }
    }

    private float[] ConvertIndexToOneHot(int index, int indexCount)
    {
        float[] result = new float[indexCount];
        result[index] = 1;
        
        return result;
    }

    private RtsAgentObservation GetAgentEnvironmentObservation()
    {
        Monitor.RemoveAllValuesFromAllTransforms();
        DetailedDebug($"Environment observed at time {Time.time}");
        
        // Vector3 camNormPos = cameraTransform.localPosition / MapSize;
        // Vector3 lastClickNorm = cameraTransform.InverseTransformPoint(lastClickWorldPos) / CameraZoom;
        // float zoomObservation = Mathf.InverseLerp(zoomMinMax.x, zoomMinMax.y, CameraZoom);
        float time = environment.timeSinceReset / environment.timeWhenReset;
        // float timeSinceDecision = timeSinceLastDecision / delayMax;
        
        float[] vectorObservations =
        {
            // camNormPos.x,
            // camNormPos.z,
            // lastClickNorm.x,
            // lastClickNorm.z,
            // zoomObservation,
            time,
            // timeSinceDecision 
        };
        
        // observationsDebugSet.Add("Cam Pos X", $"{camNormPos.x:.0}");
        // observationsDebugSet.Add("Cam Pos Z", $"{camNormPos.z:.0}");
        //
        // observationsDebugSet.Add("Cursor Pos X", $"{lastClickNorm.x:.0}");
        // observationsDebugSet.Add("Cursor Pos Z", $"{lastClickNorm.z:.0}");
        //
        // observationsDebugSet.Add("Zoom", $"{zoomObservation:.0}");
        observationsDebugSet.Add("Time", $"{time:.0}");
        // observationsDebugSet.Add("Time Since Decision", $"{timeSinceDecision:.0}");

        List<List<float[]>> observations = new List<List<float[]>>
        {
            new List<float[]>(),
            new List<float[]>(),
            new List<float[]>()
        };

        var colliders = Physics.OverlapBox(environment.transform.position, Vector3.one * MapSize, Quaternion.identity,
            interactableLayerMask);
        
        foreach (Collider col in colliders)
        {
            Vector3 relNormPos = environment.transform.InverseTransformPoint(col.transform.position) / MapSize;
            
            List<float> interactableObservation = new List<float>
            {
                relNormPos.x, 
                relNormPos.z,
            };
            
            if (col.TryGetComponent(out Reclaim reclaim))
            {
                interactableObservation.Add(reclaim.Amount / 10f);
                observations[0].Add(interactableObservation.ToArray());
                
                if (drawBufferSensorMonitor)
                {
                    Monitor.Log("Data: ", string.Join(" ", interactableObservation.ConvertAll(x => x == 0 || x == 1 ? x.ToString() : x.ToString("F1"))), col.transform);
                    Monitor.Log("Type: ", "Reclaim", col.transform);
                }
            }
            else if (col.TryGetComponent(out Unit unit))
            {
                interactableObservation.AddRange(ConvertIndexToOneHot(ownedUnits.IndexOf(unit), unitSensorComponent.MaxNumObservables));
                interactableObservation.Add(unit.assignedOrders.Count / 5f);
                
                for (int i = 0; i < numOrderObservationsPerUnit; i++)
                {
                    List<float> orderObservation;
                    
                    if (i < unit.assignedOrders.Count)
                    {
                        Vector3 relOrderNormPos = environment.transform.InverseTransformPoint(unit.assignedOrders[i].transform.position) / MapSize;
                        
                        orderObservation = new List<float>
                        {
                            relOrderNormPos.x,
                            relOrderNormPos.z,
                            1
                        };
                    }
                    else
                    {
                        orderObservation = new List<float>
                        {
                            0,
                            0,
                            0
                        };
                    }
                    
                    interactableObservation.AddRange(orderObservation);
                }
                
                if (drawBufferSensorMonitor)
                {
                    Monitor.Log("Data: ", string.Join(" ", interactableObservation.ConvertAll(x => x == 0 || x == 1 ? x.ToString() : x.ToString("F1"))), col.transform);
                    Monitor.Log("Type: ", "Unit", col.transform);
                }

                observations[1].Add(interactableObservation.ToArray());
            }
        }
        
        BroadcastObservationsCollected();
        
        return new RtsAgentObservation(vectorObservations, observations);
    }
    
    public override void CollectObservations(VectorSensor sensor)
    {
        DetailedDebug($"Observations collected at time {Time.time}");
        
        RtsAgentObservation rtsAgentObservation = isHuman ? episodeTrajectory[0].rtsAgentObservation : GetAgentEnvironmentObservation();
            
        foreach (float observation in rtsAgentObservation.vectorObservations)
        {
            sensor.AddObservation(observation);
        }

        foreach (float[] objectObservation in rtsAgentObservation.observations[0])     
        {
            reclaimSensorComponent.AppendObservation(objectObservation);
        }
        
        foreach (float[] objectObservation in rtsAgentObservation.observations[1])     
        {
            unitSensorComponent.AppendObservation(objectObservation);
        }
        
        foreach (float[] objectObservation in rtsAgentObservation.observations[2])     
        {
            orderSensorComponent.AppendObservation(objectObservation);
        }
    }

    public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
    {
        for (int i = 0; i < unitSensorComponent.MaxNumObservables; i++)
        {
            actionMask.SetActionEnabled(i, 1, i < ownedUnits.Count);
        }
    }
    
    public override void OnActionReceived(ActionBuffers actions)
    {
        base.OnActionReceived(actions);
        
        DetailedDebug($"Action received at time {Time.time}");
        
        if (!isHuman)
        {
            ActionSegment<float> continuousActions = actions.ContinuousActions;
            ActionSegment<int> discreteActions = actions.DiscreteActions;
        
            Vector2 currentCursorAction = new Vector2(
                Mathf.Clamp(continuousActions[0], -1f, 1f),
                Mathf.Clamp(continuousActions[1], -1f, 1f));
            
            selectedUnits.Clear();

            for (int i = 0; i < unitSensorComponent.MaxNumObservables; i++)
            {
                if (discreteActions[i] == 1)
                {
                    if (i < ownedUnits.Count)
                    {
                        selectedUnits.Add(ownedUnits[i]);
                    }
                    else
                    {
                        Debug.LogError("Selected nonexistent target object");
                    }
                    
                }
            }
            
            // AgentActionType currentAgentActionType = (AgentActionType)discreteActions[0];
            bool currentShiftAction = discreteActions[unitSensorComponent.MaxNumObservables] == 1;
            // int currentZoomAction = discreteActions[2] - 1;
            float timeForScheduledDecision = 2.5f;//possibleDelays[discreteActions[unitSensorComponent.MaxNumObservables + 1]];
            
            completedRtsAgentStep.rtsAgentAction = new RtsAgentAction(currentCursorAction, timeForScheduledDecision, currentShiftAction, selectedUnits);
            
            InteractWithEnvironment(completedRtsAgentStep.rtsAgentAction);
            //AddReward(-0.25f);
            //AddReward(currentZoomAction != 0 ? -0.05f : 0f);
        }
    }

    private void InteractWithEnvironment(RtsAgentAction rtsAgentAction)
    {
        // DetailedDebug($"Interacted with environment at time {Time.time}");
        // selectionBoxTransform.localScale = Vector3.zero;
        // selectionBoxTransform.localPosition = Vector3.zero;
        // lastClickWorldPos = cursorTransform.position;
        //
        // // Move cursor
        cursorTransform.localPosition = new Vector3(
            MapSize * rtsAgentAction.currentCursorAction.x,
            0,
            MapSize * rtsAgentAction.currentCursorAction.y);
        
        // if (rtsAgentAction.currentAgentActionType == AgentActionType.LeftDrag)
        // {
        //     // Left click
        //     Vector3 cursorOffset = cursorTransform.position - lastClickWorldPos;
        //     selectionBoxTransform.localScale = new Vector3(Mathf.Abs(cursorOffset.x), 1, Mathf.Abs(cursorOffset.z));
        //     selectionBoxTransform.localPosition = cursorTransform.localPosition - cursorOffset / 2;
        //
        //     if (!rtsAgentAction.currentShiftAction)
        //     {
        //         selectedUnits.Clear();
        //     }
        //
        //     foreach (Collider col in Physics.OverlapBox(selectionBoxTransform.position,
        //                  selectionBoxTransform.localScale / 2,
        //                  Quaternion.identity,
        //                  interactableLayerMask))
        //     {
        //         if (col.TryGetComponent(out Unit unit) && ownedUnits.Contains(unit) &&
        //             !selectedUnits.Contains(unit))
        //         {
        //             selectedUnits.Add(unit);
        //         }
        //     }
        // }
        // if (rtsAgentAction.currentAgentActionType == AgentActionType.RightClick)
        // {
        //     
        // }

        if (rtsAgentAction.selectedUnits.Count > 0)
        {
            Reclaim targetReclaim = null;
            float minFoundDistance = float.MaxValue;
            float currentDistance;
            
            foreach (Reclaim reclaim in environment.reclaims)
            {
                currentDistance = Vector3.Distance(reclaim.transform.position, 
                    new Vector3(
                        completedRtsAgentStep.rtsAgentAction.currentCursorAction.x, 
                        0, 
                        completedRtsAgentStep.rtsAgentAction.currentCursorAction.y) * MapSize);
                
                if (currentDistance < minFoundDistance)
                {
                    minFoundDistance = currentDistance;
                    targetReclaim = reclaim;
                }
            }

            if (targetReclaim != null)
            {
                AddReward(-minFoundDistance / MapSize);
            
                //Right click
                Ray ray = new Ray(targetReclaim.transform.position + Vector3.up * 25f, Vector3.down);

                if (Physics.Raycast(ray, out RaycastHit hitInfo, 50f, interactableLayerMask))
                {
                    float distanceFromTargetReward = 0;
                    
                    foreach (Unit selectedUnit in rtsAgentAction.selectedUnits)
                    {
                        if (rtsAgentAction.currentShiftAction)
                        {
                            if (selectedUnit.assignedOrders.Count > 0)
                            {
                                distanceFromTargetReward += Vector3.Distance(selectedUnit.assignedOrders.Last().position, targetReclaim.transform.position) / MapSize;
                            }
                            else
                            {
                                distanceFromTargetReward += Vector3.Distance(selectedUnit.transform.position, targetReclaim.transform.position) / MapSize;
                            }
                        }
                        else
                        {
                            distanceFromTargetReward += Vector3.Distance(selectedUnit.transform.position, targetReclaim.transform.position) / MapSize;
                        }

                    }   
                    
                    AddReward(-distanceFromTargetReward);
                    CreateAndAssignOrder(hitInfo, rtsAgentAction.selectedUnits, rtsAgentAction.currentShiftAction);
                }
            }
        }
        
        //Zoom camera and correct mouse
        // if (rtsAgentAction.currentZoomAction != 0)
        // {
        //     float clampedZoomOffset = Mathf.Clamp(rtsAgentAction.currentZoomAction * zoomSpeed, zoomMinMax.x - CameraZoom, zoomMinMax.y - CameraZoom);
        //
        //     if (clampedZoomOffset < 0)
        //     {
        //         cameraTransform.localPosition += cursorTransform.localPosition * ((1 - Mathf.InverseLerp(zoomMinMax.x, zoomMinMax.y, CameraZoom + clampedZoomOffset)) * 0.4f);
        //     }
        //     
        //     cursorTransform.localPosition *= (CameraZoom + clampedZoomOffset) / CameraZoom;
        //     CameraZoom += clampedZoomOffset;
        // }
        //
        // // Move Camera
        // Vector3 desiredCameraOffset = new Vector3(
        //     cur.x > 0 ? Mathf.Min(cursorOffsetCorrection.x, 0) : Mathf.Max(cursorOffsetCorrection.x, 0),
        //     0,
        //     cur.z > 0 ? Mathf.Min(cursorOffsetCorrection.z, 0) : Mathf.Max(cursorOffsetCorrection.z, 0));
        //
        // Vector3 desiredCameraPosition = cameraTransform.localPosition - desiredCameraOffset;
        //     
        // cameraTransform.localPosition = new Vector3(
        //     Mathf.Clamp(desiredCameraPosition.x, -MapSize + CameraZoom, MapSize - CameraZoom),
        //     0f,
        //     Mathf.Clamp(desiredCameraPosition.z, -MapSize + CameraZoom, MapSize - CameraZoom));
    }

    public void CreateAndAssignOrder(RaycastHit hitInfo, List<Unit> assignedUnits, bool additive)
    {
        OrderType orderType;
        bool groundOrder = false;
        Vector3 groundHitPosition = hitInfo.point;

        if (hitInfo.collider.TryGetComponent(out Reclaim reclaim))
        {
            orderType = OrderType.Reclaim;
        }
        else if (hitInfo.collider.TryGetComponent(out Unit unit) && !ownedUnits.Contains(unit))
        {
            orderType = OrderType.Attack;
        }
        else
        {
            orderType = OrderType.Move;
            groundOrder = true;
        }

        List<Unit> capableUnits = assignedUnits.Where(unit => unit.CanExecuteOrderType(orderType)).ToList();

        if (capableUnits.Count < 1) return;
        
        Order order = ObjectPooler.InstantiateGameObject(orderPrefab, groundHitPosition, Quaternion.identity, transform.parent);

        order.SetOrder(hitInfo.transform, orderType, capableUnits, groundOrder, groundHitPosition, this, additive);
    }

    public void UnitCollectedMass(float amount)
    {
        AddReward(amount);
    }

    public void HandleUnitDestroyed(IDestroyable destroyable)
    {
        Unit unit = destroyable.GetGameObject().GetComponent<Unit>();
        
        unit.OnDestroyableDestroy -= HandleUnitDestroyed;

        ownedUnits.Remove(unit);

        if (selectedUnits.Contains(unit))
        {
            selectedUnits.Remove(unit);
        }
    }

    private void DumpTrajectoryIntoHeuristicAndEndEpisode()
    {
        int count = episodeTrajectory.Count - 1;
        for (int index = 0; index < count; index++)
        {
            RequestDecision();
//            Academy.Instance.EnvironmentStep();
            episodeTrajectory.RemoveAt(0);
        }
            
        EndEpisode();
    }
    
    public void HandleEpisodeEnded()
    {
        if (isHuman)
        {
            DumpTrajectoryIntoHeuristicAndEndEpisode();
        }
        else
        {
            EndEpisode();
        }
    }
    
    public void DetailedDebug(string s)
    {
        if (detailedDebug)
        {
            Debug.Log(s);
        }
    }
}