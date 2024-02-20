using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.ComponentModel;
using System.Xml.Serialization;

using Demo3D.Common;
using Demo3D.EventQueue;
using Demo3D.PLC;
using Demo3D.PLC.Comms;
using Demo3D.Gui.AspectViewer;
using Demo3D.Visuals;
using Demo3D.Visuals.Motor;

namespace Demo3D.Components
{
    using Properties;

    [Resources(typeof(Resources))]
    [Category(nameof(Resources.Motor_Controllers_Category))]
    [HelpUrl("actuation_controller")]
    public sealed class ActuationController : ExportableVisualAspect, IController, IBindableItemOwner
    {
        private MotorAspect motor;
        private bool activate;

        private BindableItem<bool> activateBindableItem;

        private MotorAspect.Lock<MotorState> stateLock;
        private MotorAspect.Lock<MotorDirection> directionLock;

        private List<Event> events;

        [AspectProperty(IsVisible = true)]
        public override string Name
        {
            get => base.Name;
            set => base.Name = value;
        }

        [AspectProperty]
        public MotorAspect Motor
        {
            get { return motor; }
        }

        [AspectProperty]
        [DefaultValue(false)]
        public bool Activate
        {
            get { return activate; }
            set
            {
                if (ActivateBindableItem != null)
                {
                    if (ActivateBindableItem.ValueAs<bool>() != value)
                    {
                        ActivateBindableItem.Value = value;
                    }
                }

                if (SetProperty(ref activate, value))
                {
                    UpdateMotorProperties();

                    if (IsInitialized)
                    {
                        RescheduleEvents();
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<bool> ActivateBindableItem
        {
            get { return activateBindableItem; }
            private set
            {
                if (activateBindableItem != value)
                {
                    var activate = Activate;

                    if (activateBindableItem != null)
                    {
                        activateBindableItem.ValueChanged -= OnActivateBindableItemChanged;
                        activateBindableItem.DetachFromVisual();
                        activateBindableItem = null;
                    }

                    activateBindableItem = value;
                    if (activateBindableItem != null)
                    {
                        activateBindableItem.ValueChanged += OnActivateBindableItemChanged; ;
                        activateBindableItem.DefaultAccess = AccessRights.ReadFromPLC;
                        activateBindableItem.Value = activate;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public IEnumerable<BindableItem> BindableItems
        {
            get
            {
                if (ActivateBindableItem != null) { yield return ActivateBindableItem; }
            }
        }

        public ActuationController()
        {
            motor = null;
            activate = false;

            activateBindableItem = null;

            stateLock = null;
            directionLock = null;

            events = new List<Event>();
        }

        protected override void OnAssigned()
        {
            base.OnAssigned();

            CleanupBindingAPI();

            if (Visual != null)
            {
                CreateBindings();
            }
            else
            {
                RemoveBindings();
            }
        }

        protected override void OnRemoved()
        {
            if (motor != null)
            {
                motor.Controller = null; // Detach the controller from the motor.
            }

            base.OnRemoved();
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();

            UpdateMotorProperties();
            RescheduleEvents();
        }

        protected override void OnReset()
        {
            base.OnReset();

            CancelEvents();
        }

        public bool Attach(MotorAspect motor)
        {
            // If we are already attached to a motor, then the attachment should fail.
            if (this.motor == null && motor != null)
            {
                this.motor = motor;
                if (this.motor != null)
                {
                    // If we can't lock all the properties we need then the attachment should fail.
                    if (TryLockProperties())
                    {
                        // Attachment succeeded!
                        Subscribe();
                        UpdateMotorProperties(); // Set properties on the motor.
                    }
                    else
                    {
                        this.motor = null;
                        RaisePropertyChanged(nameof(Motor));
                        return false;
                    }
                }

                RaisePropertyChanged(nameof(Motor));
                return true;
            }

            return false;
        }

        public void Detach()
        {
            if (motor != null)
            {
                UnlockProperties();
                Unsubscribe();
                motor = null;

                RaisePropertyChanged(nameof(Motor));
            }
        }

        public void Pause()
        {
            CancelEvents(); // Existing event times are invalidated if the motor is paused.
        }

        public void Resume(double pauseDuration)
        {
            if (IsInitialized)
            {
                RescheduleEvents();
            }
        }

        private void CreateBindings()
        {
            Debug.Assert(Visual != null);

            ActivateBindableItem = new ReadFromServer<bool>(Visual, BindingName(nameof(Activate)));
            ActivateBindableItem.IsBindingInterface = TriStateYNM.Yes;

            UpdateBindingAPI();
        }

        private void RemoveBindings()
        {
            ActivateBindableItem = null;
            ReleaseBindingName(nameof(Activate));

            UpdateBindingAPI();
        }

        private void CancelEvents()
        {
            foreach (var ev in events)
            {
                ev?.Cancel();
            }

            events.Clear();
        }

        private void RescheduleEvents()
        {
            CancelEvents();

            if (motor != null)
            {
                var targetDirection = motor.Direction;
                var currentState = motor.Current();
                var currentDirection = (currentState.Velocity >= 0.0) ? MotionDirection.Forwards : MotionDirection.Reverse;
                var currentSpeed = Math.Abs(currentState.Velocity);
                var targetPosition = (targetDirection == MotorDirection.Forwards) ? motor.UpperPositionLimit : motor.LowerPositionLimit;
                var currentPosition = motor.Current().Position;
                var signedDisplacement = targetPosition - currentPosition;

                var motionProfile = new MotionProfile(currentDirection, currentSpeed, motor.TargetSpeed, motor.MaxAcceleration, motor.MaxDeceleration);

                var motionEvents = new List<MotionEvent>();
                var motionTime = MotionSolver.Time(signedDisplacement, motionProfile, motionEvents);
                if (motionTime >= 0.0)
                {
                    foreach (var motionEvent in motionEvents)
                    {
                        if (double.IsInfinity(motionEvent.Time) == false)
                        {
                            switch (motionEvent.Type)
                            {
                                case MotionEventType.Decelerate:
                                case MotionEventType.Stop:
                                    ScheduleMotorStateEvent(motionEvent.Time, MotorState.Off);
                                    break;
                                case MotionEventType.Accelerate:
                                case MotionEventType.Start:
                                    ScheduleMotorStateEvent(motionEvent.Time, MotorState.On);
                                    break;
                                case MotionEventType.ChangeDirectionForwards:
                                    targetDirection = MotorDirection.Forwards;
                                    ScheduleMotorDirectionEvent(motionEvent.Time, targetDirection);
                                    break;
                                case MotionEventType.ChangeDirectionReverse:
                                    targetDirection = MotorDirection.Reverse;
                                    ScheduleMotorDirectionEvent(motionEvent.Time, targetDirection);
                                    break;
                            }
                        }
                    }
                }
                else
                {
                    app.LogMessage("Warning", "Motion is unsolvable", Visual);
                }
            }
        }

        private void ScheduleMotorStateEvent(double timeFromNow, MotorState newState)
        {
            ScheduleEvent(timeFromNow, () => SetMotorState(newState));
        }

        private void ScheduleMotorDirectionEvent(double timeFromNow, MotorDirection newDirection)
        {
            ScheduleEvent(timeFromNow, () => SetMotorDirection(newDirection));
        }

        private void ScheduleEvent(double timeFromNow, Action action)
        {
            var eventQueue = document.EventQueue;
            events.Add(eventQueue.AddNotifier(timeFromNow, action));
        }

        private void SetMotorState(MotorState newState)
        {
            stateLock.Set(newState);
        }

        private void SetMotorDirection(MotorDirection newDirection)
        {
            directionLock.Set(newDirection);
        }

        private void UnlockProperties()
        {
            stateLock?.Dispose();
            directionLock?.Dispose();

            stateLock = null;
            directionLock = null;
        }

        private bool TryLockProperties()
        {
            if (stateLock == null) { stateLock = this.motor.TryLockTargetState(); }
            if (directionLock == null) { directionLock = this.motor.TryLockTargetDirection(); }

            if (stateLock == null || directionLock == null)
            {
                UnlockProperties();
                return false;
            }

            return true;
        }

        private void UpdateMotorProperties()
        {
            if (activate)
            {
                directionLock?.Set(MotorDirection.Forwards);
                stateLock?.Set(MotorState.On);
            }
            else
            {
                directionLock?.Set(MotorDirection.Reverse);
                stateLock?.Set(MotorState.On);
            }
        }

        private void Subscribe()
        {
            motor.OnTargetSpeedChanged += OnMotorTargetSpeedChanged;
            motor.OnMaxAccelerationChanged += OnMotorMaxAccelerationChanged;
            motor.OnMaxDecelerationChanged += OnMotorMaxDecelerationChanged;
            motor.OnPositionLimitChanged += OnPositionLimitChanged;
        }

        private void Unsubscribe()
        {
            motor.OnPositionLimitChanged -= OnPositionLimitChanged;
            motor.OnMaxDecelerationChanged -= OnMotorMaxDecelerationChanged;
            motor.OnMaxAccelerationChanged -= OnMotorMaxAccelerationChanged;
            motor.OnTargetSpeedChanged -= OnMotorTargetSpeedChanged;
        }

        private void OnPositionLimitChanged(MotorAspect motor, double oldMinPosition, double oldMaxPosition, double newMinPosition, double newMaxPosition)
        {
            if (IsInitialized)
            {
                RescheduleEvents();
            }
        }

        private void OnMotorMaxDecelerationChanged(MotorAspect motor, double oldMaxDeceleration, double newMaxDeceleration)
        {
            if (IsInitialized)
            {
                RescheduleEvents();
            }
        }

        private void OnMotorMaxAccelerationChanged(MotorAspect motor, double oldMaxAcceleration, double newMaxAcceleration)
        {
            if (IsInitialized)
            {
                RescheduleEvents();
            }
        }

        private void OnMotorTargetSpeedChanged(MotorAspect motor, double oldTargetSpeed, double newTargetSpeed)
        {
            if (IsInitialized)
            {
                RescheduleEvents();
            }
        }

        private void OnActivateBindableItemChanged(BindableItem item)
        {
            Activate = item.ValueAs<bool>();
        }
    }
}
