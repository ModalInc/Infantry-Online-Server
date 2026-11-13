using Axiom.Math;
using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;
using System.Collections.Generic;
using System.Linq;

namespace InfServer.Script.GameType_Multi
{   // Script Class
    /// Provides the interface between the script and bot
    ///////////////////////////////////////////////////////
    public partial class ExoLight : Bot
    {

        private List<Action> _actionQueue;

        public void fireAtEnemy(int now)
        {
            //Allows the bot to update the target every poll
            _target = null;

            //Get the closest player
            bool bClearPath = false;
            _target = getTargetPlayer(ref bClearPath, _targetTeam);


            if (_target != null)
            {
                if (bClearPath)
                {   //What is our distance to the target?
                    double distance = (_state.position() - _target._state.position()).Length;
                    bool bFleeing = false;

                    //Too far?
                    if (distance > pursueDist)
                        ApplySteer(steerForPersuePlayer);
                    //Quite short?
                    else if (distance < shortDist)
                    {
                        ApplySteer(delegate (InfantryVehicle vehicle)
                        {
                            if (_target != null)
                                return vehicle.SteerForFlee(_target._state.position());
                            else
                                return Vector3.Zero;
                        }, skipBodyRotate: true);
                    }
                    //Just right
                    else
                        HoldPosition();




                    if (!bFleeing)
                    {
                        //Should we be firing our MGs?
                        if (distance <= fireDist && distance > incinDist)
                        {
                            if (_weapon.ItemID != AssetManager.Manager.getItemByID(_type.InventoryItems[0]).id)
                                _weapon.equip(AssetManager.Manager.getItemByID(_type.InventoryItems[0]));

                            if (_weapon.ableToFire())
                            {
                                int aimResult = _weapon.getAimAngle(_target._state);

                                if (_weapon.isAimed(aimResult))
                                {   //Spot on! Fire?
                                    _itemUseID = _weapon.ItemID;
                                    _weapon.shotFired();
                                }

                                SetAim(aimResult);
                            }
                        }
                        //Should we be firing our incin?
                        else if (distance <= incinDist && distance > stompDist)
                        {
                            if (_weapon.ItemID != AssetManager.Manager.getItemByID(_type.InventoryItems[1]).id)
                                _weapon.equip(AssetManager.Manager.getItemByID(_type.InventoryItems[1]));

                            if (_weapon.ableToFire())
                            {
                                int aimResult = _weapon.getAimAngle(_target._state);

                                if (_weapon.isAimed(aimResult))
                                {   //Spot on! Fire?
                                    _itemUseID = _weapon.ItemID;
                                    _weapon.shotFired();
                                }

                                SetAim(aimResult);
                            }

                        }
                        //Should we be firing our melee?
                        else if (distance <= stompDist)
                        {
                            if (_weapon.ItemID != AssetManager.Manager.getItemByID(_type.InventoryItems[2]).id)
                                _weapon.equip(AssetManager.Manager.getItemByID(_type.InventoryItems[2]));

                            if (_weapon.ableToFire())
                            {
                                int aimResult = _weapon.getAimAngle(_target._state);

                                if (_weapon.isAimed(aimResult))
                                {   //Spot on! Fire?
                                    _itemUseID = _weapon.ItemID;
                                    _weapon.shotFired();
                                }

                                SetAim(aimResult);
                            }

                        }
                        else
                            SetAim(0, false);
                    }
                }
                else
                {
                    //Navigate to him
                    navigateTo(_target._state, steerForPersuePlayer, PathPriority.Combat);
                }


            }
        }

        public void pushToEnemyFlag(int now)
        {
            Arena.FlagState targetFlag;
            List<Arena.FlagState> enemyflags;
            List<Arena.FlagState> flags;

            flags = _arena._flags.Values.OrderBy(f => f.posX).ToList();

            if (_team._name == "Titan Militia")
            {
                enemyflags = flags.Where(f => f.team != _team).Take(3).ToList();
            }
            else
            {
                enemyflags = flags.Where(f => f.team != _team).ToList();
            }

            if (enemyflags.Count >= 3)
                targetFlag = enemyflags[_rand.Next(0, 2)];
            else
                targetFlag = enemyflags[0];


            Helpers.ObjectState target = new Helpers.ObjectState();
            target.positionX = targetFlag.posX;
            target.positionY = targetFlag.posY;



            //What is our distance to the target?
            double distance = (_state.position() - target.position()).Length;


            //Navigate to him
            navigateTo(target, steerForPersuePlayer, PathPriority.Idle);
        }
        public void patrolBetweenFlags(int now)
        {
            Arena.FlagState friendlyflag;
            Arena.FlagState targetFlag;
            List<Arena.FlagState> enemyflags;
            List<Arena.FlagState> flags;

            flags = _arena._flags.Values.OrderBy(f => f.posX).ToList();

            if (_team._name == "Titan Militia")
            {
                friendlyflag = flags.Where(f => f.team == _team).Last();
                enemyflags = flags.Where(f => f.team != _team).Take(3).ToList();
            }
            else
            {
                friendlyflag = _arena._flags.Values.OrderBy(f => f.posX).Where(f => f.team == _team).First();
                enemyflags = _arena._flags.Values.OrderByDescending(f => f.posX).Where(f => f.team != _team).Take(2).ToList();
            }

            if (enemyflags.Count >= 3)
                targetFlag = enemyflags[_rand.Next(0, 1)];
            else
                targetFlag = enemyflags[0];





            Helpers.ObjectState target = new Helpers.ObjectState();
            if (_bPatrolEnemy)
            {
                target.positionX = targetFlag.posX;
                target.positionY = targetFlag.posY;
            }
            else
            {
                target.positionX = friendlyflag.posX;
                target.positionY = friendlyflag.posY;
            }


            //What is our distance to the target?
            double distance = (_state.position() - target.position()).Length;

            //Are we there yet?
            if (distance < patrolDist)
            {
                //change our direction
                _bPatrolEnemy = !_bPatrolEnemy;
            }

            //Navigate to him
            navigateTo(target, steerForPersuePlayer, PathPriority.Idle);
        }


        public void wander(int now)
        {
            if (_targetPoint == null)
                _targetPoint = getTargetPoint();


            //What is our distance to the target?
            double distance = (_state.position() - _targetPoint.position()).Length;

            //Are we there yet?
            if (distance < patrolDist)
            {
                _targetPoint = null;
                return;
            }

            //Navigate to him
            navigateTo(_targetPoint, steerForPersuePlayer, PathPriority.Idle);
        }


        public Helpers.ObjectState getTargetPoint()
        {
            Helpers.ObjectState target = new Helpers.ObjectState();

            Arena.FlagState targetFlag;
            List<Arena.FlagState> flags;

            flags = _arena._flags.Values.OrderBy(f => f.posX).ToList();
            int flagCount = flags.Where(f => f.team == _team).Count();


            if (flagCount > 2)
                targetFlag = _arena._flags.Values.OrderBy(f => f.posX).Where(f => f.team == _team).First();
            else
                targetFlag = flags.Where(f => f.team != _team).Last();

            int blockedAttempts = 30;
            short pX;
            short pY;
            while (true)
            {
                pX = targetFlag.posX;
                pY = targetFlag.posY;
                Helpers.randomPositionInArea(_arena, 3000, ref pX, ref pY);
                if (_arena.getTile(pX, pY).Blocked)
                {
                    blockedAttempts--;
                    if (blockedAttempts <= 0)
                        //Consider the spawn to be blocked
                        return null;
                    continue;
                }

                target.positionX = targetFlag.posX;
                target.positionY = pY;
                break;
            }
            return target;
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
                fireAtEnemy,
                retreat
            }
        }
    }
}
