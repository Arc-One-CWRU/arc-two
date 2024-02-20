using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using Microsoft.DirectX;

using Demo3D.Utilities;
using Demo3D.Visuals;
using Demo3D.Common;

namespace Demo3D.Components
{
    using Properties;

    [Resources(typeof(Resources))]
    [Category(nameof(Resources.Loads_Category))]
    [HelpUrl("distance_load_source")]
    public class DistanceLoadSource : LoadSource
    {
        private IMotor motor = null;
        private double nextReleaseDistance = 0;
        private object distanceNotifier = null;
        private IConveyor conveyor = null;
        private double distance = 1.0;
        private bool initialConveyorVelocity = false;

        private IMotor Motor
        {
            get { return motor; }
            set
            {
                var oldMotor = motor;
                if (SetProperty(ref motor, value))
                {
                    if (oldMotor != null)
                    {
                        oldMotor.DirectionListeners -= OnDirectionChanged;
                        oldMotor.OnNotifyDistanceListeners -= OnNotifyDistance;
                    }

                    if (motor != null)
                    {
                        motor.DirectionListeners -= OnDirectionChanged;
                        motor.DirectionListeners += OnDirectionChanged;

                        motor.OnNotifyDistanceListeners -= OnNotifyDistance;
                        motor.OnNotifyDistanceListeners += OnNotifyDistance;
                    }
                }
            }
        }

        [Required]
        public IConveyor Conveyor
        {
            get { return conveyor; }
            set
            {
                var oldConveyor = conveyor;
                if (SetProperty(ref conveyor, value))
                {
                    Motor = (conveyor != null) ? conveyor.Motor : null;

                    if (oldConveyor != null)
                    {
                        oldConveyor.OnMotorChanged -= OnMotorChanged;
                    }

                    if (conveyor != null)
                    {
                        conveyor.OnMotorChanged += OnMotorChanged;
                    }
                }
            }
        }

        [Distance]
        [DefaultValue(1.0)]
        public double Distance
        {
            get { return distance; }
            set { SetProperty(ref distance, value); }
        }

        [DefaultValue(false)]
        public bool InitialConveyorVelocity
        {
            get { return initialConveyorVelocity; }
            set { SetProperty(ref initialConveyorVelocity, value); }
        }

        protected override void OnRemoved()
        {
            if (Visual != null)
            {
                var sensor = Visual.FindAspect<CollisionSensorAspect>();
                if (sensor != null)
                {
                    sensor.OnCleared -= SensorAspect_OnCleared;
                }
            }

            if (Conveyor != null)
            {
                Conveyor.OnMotorChanged -= OnMotorChanged;
            }

            Motor = null;

            base.OnRemoved();
        }

        protected override void OnEnabled()
        {
            base.OnEnabled();

            if (Motor != null)
            {
                motor.DirectionListeners -= OnDirectionChanged;
                motor.DirectionListeners += OnDirectionChanged;

                Motor.OnNotifyDistanceListeners -= OnNotifyDistance;
                Motor.OnNotifyDistanceListeners += OnNotifyDistance;

                if (distanceNotifier != null)
                {
                    motor.RemoveDistanceNotifier(distanceNotifier);
                    distanceNotifier = null;
                }

                nextReleaseDistance = Motor.DistanceTravelled + Distance * (int)Motor.Direction;
                distanceNotifier = Motor.AddDistanceNotifier(nextReleaseDistance, null);
            }
        }

        protected override void OnDisabled()
        {
            if (motor != null)
            {
                if (distanceNotifier != null)
                {
                    motor.RemoveDistanceNotifier(distanceNotifier);
                    distanceNotifier = null;
                }

                motor.DirectionListeners -= OnDirectionChanged;
                motor.OnNotifyDistanceListeners -= OnNotifyDistance;
            }

            base.OnDisabled();
        }

        protected override void OnReset()
        {
            base.OnReset();

            if (motor != null)
            {
                if (distanceNotifier != null)
                {
                    motor.RemoveDistanceNotifier(distanceNotifier);
                    distanceNotifier = null;
                }

                motor.DirectionListeners -= OnDirectionChanged;
                motor.OnNotifyDistanceListeners -= OnNotifyDistance;
            }
        }

        protected override void OnInitialize()
        {
            if (Visual == null || Conveyor == null) { return; }

            var sensor = Visual.FindAspect<CollisionSensorAspect>();
            if (sensor == null) { return; }

            sensor.OnCleared -= SensorAspect_OnCleared;
            sensor.OnCleared += SensorAspect_OnCleared;

            motor.DirectionListeners -= OnDirectionChanged;
            motor.DirectionListeners += OnDirectionChanged;

            motor.OnNotifyDistanceListeners -= OnNotifyDistance;
            motor.OnNotifyDistanceListeners += OnNotifyDistance;

            // Schedule first load creation
            if (IsEnabled)
            {
                document.Run(ReleaseLoad);
            }
        }

        private void SensorAspect_OnCleared(Visual sender)
        {
            if (IsEnabled)
            {
                var sensor = Visual.FindAspect<CollisionSensorAspect>();
                if (sensor != null && sensor.IsBlocked == false)
                {
                    if (Motor != null)
                    {
                        if (distanceNotifier != null)
                        {
                            motor.RemoveDistanceNotifier(distanceNotifier);
                            distanceNotifier = null;
                        }

                        nextReleaseDistance = Motor.DistanceTravelled + Distance * (int)Motor.Direction;
                        distanceNotifier = Motor.AddDistanceNotifier(nextReleaseDistance, null);
                    }
                }
            }
        }

        private void ReleaseLoad()
        {
            // Clone the load creator
            var clone = CloneVisual();

            if (InitialConveyorVelocity)
            {
                if (Conveyor != null)
                {
                    var worldDirection = Vector3.TransformNormal(Conveyor.MovementDirectionAt(Vector3.TransformCoordinate(clone.WorldLocation, Conveyor.WorldToObjectMatrix())), Conveyor.ObjectToWorldMatrix());
                    document.Run(() => clone.Velocity = worldDirection * Conveyor.Motor.CurrentSpeed);
                }
            }
        }

        private void OnDirectionChanged(object sender, MotorDirection oldDirection, MotorDirection newDirection)
        {
            if (distanceNotifier != null)
            {
                motor.RemoveDistanceNotifier(distanceNotifier);
                distanceNotifier = null;
            }

            nextReleaseDistance += Distance * (int)newDirection * 2;
            distanceNotifier = Motor.AddDistanceNotifier(nextReleaseDistance, null);
        }

        private void OnNotifyDistance(Visual sender, NotifyDistanceInfo info)
        {
            if (IsEnabled && info.Distance >= nextReleaseDistance)
            {
                ReleaseLoad();
            }
        }

        private void OnMotorChanged(IConveyor sender, IMotor oldMotor, IMotor newMotor)
        {
            Motor = newMotor;
        }
    }
}
