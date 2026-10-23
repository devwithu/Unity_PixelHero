using System;
using System.Collections.Generic;
using PixelHero.Common;
using PixelHero.Js;

namespace PixelHero.Entities
{
    /// <summary>
    /// client/src/entity/character/player/handler.ts: the main player's handler. It reports every walk to the server
    /// (Movement Request / Started / Entity / Step / Stop, then Target) and follows the camera.
    /// </summary>
    public sealed class PlayerHandler : CharacterHandler
    {
        readonly Player _player;
        int _lastStepX = -1;
        int _lastStepY = -1;

        public PlayerHandler(Player player, World world) : base(player, world) => _player = player;

        protected override List<int[]> HandleRequestPath(int x, int y)
        {
            var map = World.Map;

            if (_player.GridX == x && _player.GridY == y) return new List<int[]>();
            if (_player.Dead || _player.Frozen) return new List<int[]>();

            if (!_player.HasKeyboardMovement() && _player.Target == null) _player.LookAtPosition(x, y);

            if (_player.CanAttackTarget() && !_player.Trading) return new List<int[]>();

            var isObject = map.IsObject(x, y);
            var isResource = _player.Target?.IsResource == true;
            var isNPC = _player.Target?.IsNPC == true;

            if (map.IsColliding(x, y) && !isObject && !isResource && !isNPC) return new List<int[]>();

            var request = new Dictionary<string, object>
            {
                ["opcode"] = Opcodes.Movement.Request,
                ["requestX"] = x,
                ["requestY"] = y,
                ["playerX"] = _player.GridX,
                ["playerY"] = _player.GridY
            };
            if (_player.Target != null) request["targetInstance"] = _player.Target.Instance;
            request["following"] = _player.Following;
            World.Send(Packets.Movement, request);

            var ignores = new List<Maps.PathFinder.TileIgnore>();
            var cursor = "";

            if (isObject)
            {
                ignores.Add(new Maps.PathFinder.TileIgnore { X = x, Y = y });
                cursor = map.GetTileCursor(x, y);
                if (cursor == "fishing")
                    ignores.AddRange(new[]
                    {
                        new Maps.PathFinder.TileIgnore { X = x + 1, Y = y }, new Maps.PathFinder.TileIgnore { X = x - 1, Y = y },
                        new Maps.PathFinder.TileIgnore { X = x, Y = y + 1 }, new Maps.PathFinder.TileIgnore { X = x, Y = y - 1 }
                    });
            }

            if (isResource || isNPC) ignores.Add(new Maps.PathFinder.TileIgnore { X = x, Y = y });

            return World.FindPath(_player, x, y, ignores, cursor);
        }

        protected override void HandleStartPathing(List<int[]> path)
        {
            var input = World.Input;
            var last = path[path.Count - 1];
            input.SelectedX = last[0];
            input.SelectedY = last[1];
            _player.Moving = true;
            input.SelectedCellVisible = true;

            var started = new Dictionary<string, object>
            {
                ["opcode"] = Opcodes.Movement.Started,
                ["requestX"] = input.SelectedX,
                ["requestY"] = input.SelectedY,
                ["playerX"] = _player.GridX,
                ["playerY"] = _player.GridY,
                ["movementSpeed"] = (object)_player.MovementSpeed ?? Undefined.Value
            };
            if (_player.Target != null) started["targetInstance"] = _player.Target.Instance;
            World.Send(Packets.Movement, started);
        }

        protected override void HandleStopPathing(int x, int y, bool forced)
        {
            World.Entities.RegisterPosition(_player);
            World.Input.SelectedCellVisible = false;

            var stop = new Dictionary<string, object>
            {
                ["opcode"] = Opcodes.Movement.Stop,
                ["playerX"] = _player.GridX,
                ["playerY"] = _player.GridY
            };
            var targetInstance = _player.Target?.Instance;
            if (string.IsNullOrEmpty(targetInstance)) targetInstance = World.GetEntityAt(x, y)?.Instance;
            if (targetInstance != null) stop["targetInstance"] = targetInstance;
            stop["orientation"] = _player.Orientation;
            World.Send(Packets.Movement, stop);

            if (_player.Trading)
            {
                var trade = new Dictionary<string, object> { ["opcode"] = Opcodes.Trade.Request };
                if (_player.Target != null) trade["instance"] = _player.Target.Instance;
                World.Send(Packets.Trade, trade);
            }

            var targetType = GetTargetType();
            if (targetType != Opcodes.Target.None)
                World.Send(Packets.Target, new List<object> { GetTargetType(), _player.Target?.Instance ?? "" });

            if (_player.Target != null)
            {
                _player.LookAt(_player.Target);
                if (_player.Target.IsObject) _player.RemoveTarget();
                _player.DisableAction = true;
            }

            World.Input.SetPassiveTarget();

            if (!_player.HasKeyboardMovement()) _player.PerformAction(_player.Orientation, Modules.Actions.Idle);

            _player.Moving = false;
            _player.Trading = false;
        }

        protected override void HandleStep()
        {
            if (_player.HasNextStep()) World.Entities.RegisterPosition(_player);

            if (_lastStepX == _player.GridX && _lastStepY == _player.GridY) return;

            HandleAttackers();
            HandleFollowers();

            if (_player.HasTarget())
                World.Send(Packets.Movement, new Dictionary<string, object>
                {
                    ["opcode"] = Opcodes.Movement.Entity,
                    ["targetInstance"] = _player.Target.Instance,
                    ["requestX"] = _player.Target.GridX,
                    ["requestY"] = _player.Target.GridY
                });

            World.Send(Packets.Movement, new Dictionary<string, object>
            {
                ["opcode"] = Opcodes.Movement.Step,
                ["playerX"] = _player.GridX,
                ["playerY"] = _player.GridY,
                ["nextGridX"] = _player.NextGridX,
                ["nextGridY"] = _player.NextGridY,
                ["timestamp"] = JsValue.ToInt32(World.PerformanceNow() - World.TimeOffset)
            });

            _lastStepX = _player.GridX;
            _lastStepY = _player.GridY;

            if (_player.CanAttackTarget()) _player.Stop();
        }

        protected override void HandleMove()
        {
            if (!World.Camera.IsCentered()) return;
            World.Camera.CentreOn(_player);
        }

        Opcodes.Target GetTargetType()
        {
            var target = _player.Target;
            if (target == null) return Opcodes.Target.None;
            if (target.IsNPC || target.IsChest) return Opcodes.Target.Talk;
            if (target.IsObject || target.IsResource) return Opcodes.Target.Object;
            if (target.IsMob || (target.IsPlayer && World.Pvp)) return Opcodes.Target.Attack;
            return Opcodes.Target.None;
        }
    }

    /// <summary>A position in the world: pixels (x, y) and the tile (gridX, gridY), like the client's Coordinate.</summary>
    public struct WorldPosition
    {
        public double X;
        public double Y;
        public int GridX;
        public int GridY;
    }

    /// <summary>
    /// The movement part of client/src/controllers/input.ts: clicks (handleLeftClick → move), WASD (keyMove from the
    /// updater), target selection and the selected-cell state the renderer shows. Menus are a later stage.
    /// </summary>
    public sealed class InputController
    {
        readonly World _world;

        public Entity Entity;
        public bool KeyMovement;
        public int SelectedX = -1;
        public int SelectedY = -1;
        public bool SelectedCellVisible;
        /// <summary>targetAnimation row: 0 passive, 1 interaction, 2 attack.</summary>
        public int TargetRow;

        public InputController(World world) => _world = world;

        Player PlayerEntity => _world.Player;

        /// <summary>handleLeftClick without Ctrl (admin teleport).</summary>
        public void Click(WorldPosition position)
        {
            KeyMovement = false;
            PlayerEntity.DisableAction = false;
            Move(position);
        }

        /// <summary>handleKeyDown / handleKeyUp for the movement keys.</summary>
        public void SetMovementKey(Modules.Orientation direction, bool down)
        {
            switch (direction)
            {
                case Modules.Orientation.Up: PlayerEntity.MoveUp = down; break;
                case Modules.Orientation.Down: PlayerEntity.MoveDown = down; break;
                case Modules.Orientation.Left: PlayerEntity.MoveLeft = down; break;
                case Modules.Orientation.Right: PlayerEntity.MoveRight = down; break;
            }

            if (!down) PlayerEntity.DisableAction = false;
        }

        public void KeyMove(WorldPosition position)
        {
            if (PlayerEntity.HasPath()) return;
            KeyMovement = true;
            Move(position);
        }

        public void Move(WorldPosition position) => Move(position, !KeyMovement);

        public void Move(WorldPosition position, bool useSearch)
        {
            var player = PlayerEntity;
            if (player.Stunned || player.Teleporting) return;

            SetPassiveTarget();

            if (player.DisableAction) return;
            if (_world.Map.IsOutOfBounds(position.GridX, position.GridY)) return;

            if (_world.Map.IsObject(position.GridX, position.GridY))
            {
                SetInteractionTarget();
                player.SetObjectTarget(position.GridX, position.GridY);
                return;
            }

            player.RemoveTarget();

            Entity = GetEntity(position, useSearch);

            if (Entity != null && Entity.Instance != player.Instance)
            {
                if (Entity.IsMob || Entity.IsPlayer) SetAttackTarget();
                else SetInteractionTarget();

                if (IsTargetable(Entity))
                {
                    player.Follow(Entity);

                    if (IsAttackable(Entity))
                    {
                        ((Character)Entity).AddAttacker(player);
                        if (player.CanAttackTarget())
                            _world.Send(Packets.Target, new List<object>
                            {
                                Opcodes.Target.Attack, Entity.Instance, Entity.GridX, Entity.GridY
                            });
                    }

                    return;
                }
            }

            if (player.IsStunned()) return;

            player.Go(position.GridX, position.GridY);
        }

        Entity GetEntity(WorldPosition position, bool useSearch)
        {
            var entity = useSearch ? _world.SearchForEntityAt(position) : _world.GetEntityAt(position.GridX, position.GridY);

            if (Entity != null && (entity == null || entity.Instance != Entity.Instance)) Entity.UpdateSilhouette();

            return entity;
        }

        public void SetPassiveTarget() => TargetRow = 0;
        void SetInteractionTarget() => TargetRow = 1;
        void SetAttackTarget() => TargetRow = 2;

        bool IsTargetable(Entity entity) => IsAttackable(entity) || entity.IsNPC || entity.IsChest || entity.IsResource;

        bool IsAttackable(Entity entity) => entity.IsMob || (entity.IsPlayer && _world.Pvp);

        /// <summary>input.ts getCoords: a screen (canvas) pixel to world pixels and the tile.</summary>
        public WorldPosition GetCoords(double mouseX, double mouseY)
        {
            var camera = _world.Camera;
            var offsetX = mouseX % camera.ZoomFactor;
            var offsetY = mouseY % camera.ZoomFactor;
            var x = (mouseX - offsetX) / camera.ZoomFactor + camera.X;
            var y = (mouseY - offsetY) / camera.ZoomFactor + camera.Y;
            return new WorldPosition
            {
                X = x,
                Y = y,
                GridX = JsValue.ToInt32(x / _world.TileSize),
                GridY = JsValue.ToInt32(y / _world.TileSize)
            };
        }

        /// <summary>updater.ts updateKeyboard: one tile per idle frame while a movement key is held.</summary>
        public void UpdateKeyboard()
        {
            var player = PlayerEntity;
            var position = new WorldPosition { X = -1, Y = -1, GridX = player.GridX, GridY = player.GridY };

            if (player.Moving || player.Teleporting || player.Frozen || !player.HasKeyboardMovement()) return;

            if (player.MoveUp) position.GridY--;
            else if (player.MoveDown) position.GridY++;
            else if (player.MoveRight) position.GridX++;
            else if (player.MoveLeft) position.GridX--;

            KeyMove(position);
        }
    }
}
