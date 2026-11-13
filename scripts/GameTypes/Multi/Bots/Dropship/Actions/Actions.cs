using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;


using InfServer.Game;
using InfServer.Protocol;
using InfServer.Scripting;
using InfServer.Bots;

using Assets;
using Axiom.Math;
using Bnoerj.AI.Steering;

namespace InfServer.Script.GameType_Multi
{   // Script Class
    /// Provides the interface between the script and bot
    ///////////////////////////////////////////////////////
    public partial class Dropship : Bot
    {

        private List<Action> _actionQueue;

        public void moveToDrop(int now)
        {
            bool onSite = false;


            //Find the place we want to land at
            List<Vehicle> inTrackingRange = _arena.getVehiclesInRange(_state.positionX, _state.positionY, 10000000);

            foreach (Vehicle v in inTrackingRange)
            {
                if (v._type.Id == 406 && v._team == _team)
                    _targetLocation = v;
            }
            //Get to landing zone
            if (Helpers.distanceTo(_targetLocation._state, _state) > 45)
                ApplySteer(steerForFollowOwner);
            else
            {
                freezeMovement(5000);
                onSite = true;
            }

            //Can we drop?
            if (onSite && !_bDropped)
            {
                _itemUseID = _weapon.ItemID;
                _weapon.shotFired();
                _bDropped = true;
            }

            navigateTo(_targetLocation._state, steerForFollowOwner, PathPriority.Idle);
        }

        public void leaveMap(int now)
        {
            bool onSite = false;


            //Find the place we want to land at
            List<Vehicle> inTrackingRange = _arena.getVehiclesInRange(_state.positionX, _state.positionY, 10000000);

            foreach (Vehicle v in inTrackingRange)
            {
                if (v._type.Id == 407 && v._team == _team)
                    _targetLocation = v;
            }
            //Get to landing zone
            if (Helpers.distanceTo(_targetLocation._state, _state) > 45)
                ApplySteer(steerForFollowOwner);
            else
            {
                freezeMovement(5000);
                onSite = true;
            }
            //Cya
            if (onSite && !_bDropped)
            {
                base.destroy(true);
            }


            navigateTo(_targetLocation._state, steerForPersuePlayer, PathPriority.Idle);
        }



        public class Action
        {
            public Priority priority;
            public Type type;

            public Action(Priority pr, Type ty)
            {
                type = ty;
                priority = pr;
            }

            public enum Priority
            {
                None,
                Low,
                Medium,
                High
            }

            public enum Type
            {
                moveToDrop,
                dropPayload,
                fireAtEnemy,
                leaveMap
            }
        }
    }
}
