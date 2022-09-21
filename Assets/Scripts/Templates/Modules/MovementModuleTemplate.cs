﻿using Systems.Modules;
using UnityEngine;

namespace Templates.Modules
{
    [CreateAssetMenu(menuName = "Create ExecutingMoveOrderState", fileName = "ExecutingMoveOrderState")]
    public class MovementModuleTemplate : OrderModuleTemplate
    {
        public float defaultMovementSpeed;
        public float defaultStoppingDistance;
        
        public override Module GetModule()
        {
            return new MoveOrderExecutionModule(this);
        }
    }
}