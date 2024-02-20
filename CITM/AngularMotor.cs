using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Collections.Generic;
using System.Xml.Serialization;

using Demo3D.Visuals;
using Demo3D.PLC;
using Demo3D.PLC.Comms;
using Demo3D.Common;
using Demo3D.Utilities;
using Demo3D.EventQueue;
using Demo3D.Gui.AspectViewer;
using Demo3D.Gui.AspectViewer.Editors;

using KJEUtil = Demo3D.Visuals.KJE.Utilities;

namespace Demo3D.Components
{
    using Properties;

    public enum AngularMotorControlMode
    {
        None,
        OnOff,
        ForwardReverse,
        Activate,
        Target
    }

    public enum AngularMotorTargetType
    {
        Discontinuous,
        Continuous
    }

    [Resources(typeof(Resources))]
    [Category(nameof(Resources.Motors_Category))]
    [HelpUrl("angular_motor")]
    [Obsolete]
    public class AngularMotor : ExportableVisualAspect, IMotor, IBindableItemOwner
    {
        private readonly Motor<AngularMotorProperties> motor = new Motor<AngularMotorProperties>() { Acceleration = 0, Deceleration = 0, Speed = 90 };
        private AngularMotorControlMode controlMode = AngularMotorControlMode.None;
        private bool targetEnabled = false;
        private double target = 0;
        private AngularMotorTargetType targetType = AngularMotorTargetType.Discontinuous;
        private bool angleFeedback = false;
        private double angleResolution = 5;
        private double angleLowerBoundary = double.NegativeInfinity;
        private double angleUpperBoundary = double.PositiveInfinity;
        private object angleLowerBoundaryNotifier = null;
        private object angleUpperBoundaryNotifier = null;
        private BindableItem targetBindableItem;
        private BindableItem angleBindableItem;
        private BindableItem activateBindableItem;
        private List<Event> events = new List<Event>();

        public event MotorStatePropertyChanged StateListeners
        {
            add { motor.StateListeners += value; }
            remove { motor.StateListeners -= value; }
        }

        public event MotorDirectionPropertyChanged DirectionListeners
        {
            add { motor.DirectionListeners += value; }
            remove { motor.DirectionListeners -= value; }
        }

        public event MotorSpeedChanged SpeedListeners
        {
            add { motor.SpeedListeners += value; }
            remove { motor.SpeedListeners -= value; }
        }

        public event MotorSpeedChanged AccelListeners
        {
            add { motor.AccelListeners += value; }
            remove { motor.AccelListeners -= value; }
        }

        public event MotorSpeedChanged DecelListeners
        {
            add { motor.DecelListeners += value; }
            remove { motor.DecelListeners -= value; }
        }

        public event NotifyDistanceListener OnNotifyDistanceListeners
        {
            add { motor.OnNotifyDistanceListeners += value; }
            remove { motor.OnNotifyDistanceListeners -= value; }
        }

        [AspectProperty]
        [DefaultValue(AngularMotorControlMode.None)]
        public AngularMotorControlMode ControlMode
        {
            get { return controlMode; }
            set
            {
                if (SetProperty(ref controlMode, value))
                {
                    // Automatically set target enabled if the control mode is set to target.
                    if (controlMode == AngularMotorControlMode.Target)
                    {
                        TargetEnabled = true;
                    }

                    UpdateBindings();
                    RaisePropertyChanged(nameof(ControlModeNotTarget));
                }
            }
        }

        [Browsable(false)]
        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public bool ControlModeNotTarget
        {
            get { return ControlMode != AngularMotorControlMode.Target; }
        }

        [AspectProperty]
        [DefaultValue(false)]
        [AspectEditor(IsEnabledPropertyLink = nameof(ControlModeNotTarget))]
        [Cat("Target")]
        public bool TargetEnabled
        {
            get { return targetEnabled; }
            set
            {
                // We don't allow disabling target if the control mode is set to target.
                if (SetProperty(ref targetEnabled, (ControlMode != AngularMotorControlMode.Target) ? value : true))
                {
                    if (targetEnabled)
                    {
                        InitialState = MotorState.Off;
                        DriveToTarget();
                    }
                    else
                    {
                        CancelEvents();
                    }

                    RaisePropertyChanged(nameof(TargetNotEnabled));
                }
            }
        }

        [Browsable(false)]
        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public bool TargetNotEnabled
        {
            get { return TargetEnabled == false; }
        }

        [AspectProperty]
        [AspectEditor(IsVisiblePropertyLink = nameof(TargetEnabled), IsEnabledPropertyLink = nameof(TargetEnabled))]
        [DefaultValue(0)]
        [Cat("Target")]
        [Angle]
        public double Target
        {
            get
            {
                if (TargetBindableItem != null)
                {
                    target = TargetBindableItem.ValueAs<double>();
                }

                return target;
            }

            set
            {
                if (TargetBindableItem != null)
                {
                    TargetBindableItem.Value = value;
                    RaisePropertyChanged(nameof(Target));
                }
                else
                {
                    SetTargetAndNotify(value);
                }
            }
        }

        [AspectProperty]
        [AspectEditor(IsVisiblePropertyLink = nameof(TargetEnabled), IsEnabledPropertyLink = nameof(TargetEnabled))]
        [DefaultValue(AngularMotorTargetType.Discontinuous)]
        [Cat("Target")]
        public AngularMotorTargetType TargetType
        {
            get { return targetType; }

            set
            {
                if (SetProperty(ref targetType, value))
                {
                    if (targetEnabled)
                    {
                        DriveToTarget();
                    }
                }
            }
        }

        [AspectProperty]
        [DefaultValue(false)]
        [Cat("Feedback")]
        public bool AngleFeedback
        {
            get { return angleFeedback; }

            set
            {
                if (SetProperty(ref angleFeedback, value))
                {
                    UpdateAngleNotifiers();
                    UpdateBindings();
                }
            }
        }

        [AspectProperty]
        [AspectEditor(IsVisiblePropertyLink = nameof(AngleFeedback), IsEnabledPropertyLink = nameof(AngleFeedback))]
        [Angle]
        [DefaultValue(5.0)]
        [Cat("Feedback")]
        public double AngleResolution
        {
            get { return angleResolution; }

            set
            {
                if (SetProperty(ref angleResolution, Math.Max(value, 0.1)))
                {
                    UpdateAngleNotifiers();
                }
            }
        }

        [XmlIgnore]
        [AspectProperty]
        [AspectEditor(IsVisiblePropertyLink = nameof(AngleFeedback), IsEnabledPropertyLink = nameof(AngleFeedback))]
        [Angle]
        [Cat("Feedback")]
        public double Angle
        {
            get
            {
                if (AngleFeedback && AngleBindableItem != null)
                {
                    return AngleBindableItem.ValueAs<double>();
                }

                return DistanceTravelled;
            }
        }

        [AspectProperty]
        [Angle]
        [DefaultValue(0)]
        public double InitialAngle
        {
            get { return motor.InitialDistanceTravelled; }
            set { motor.InitialDistanceTravelled = value; }
        }

        IEnumerable<BindableItem> IBindableItemOwner.BindableItems
        {
            get
            {
                if (StateBindableItem != null) yield return StateBindableItem;
                if (OnForwardsBindableItem != null) yield return OnForwardsBindableItem;
                if (OnReverseBindableItem != null) yield return OnReverseBindableItem;
                if (ActivateBindableItem != null) yield return ActivateBindableItem;
                if (TargetBindableItem != null) yield return TargetBindableItem;
                if (AngleBindableItem != null) yield return AngleBindableItem;
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem StateBindableItem { get { return motor.StateBindableItem; } }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem ForwardsBindableItem { get { return motor.ForwardsBindableItem; } }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem OnForwardsBindableItem { get { return motor.OnForwardsBindableItem; } }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem OnReverseBindableItem { get { return motor.OnReverseBindableItem; } }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem ActivateBindableItem
        {
            get { return activateBindableItem; }
            private set
            {
                activateBindableItem = value;
                activateBindableItem.Value = Activate;
                activateBindableItem.ValueChanged += ActivateBindableItem_ValueSourceChangedListeners;
                activateBindableItem.DefaultAccess = AccessRights.ReadFromPLC;
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem TargetBindableItem
        {
            get { return targetBindableItem; }
            private set
            {
                targetBindableItem = value;
                targetBindableItem.Value = Target;
                targetBindableItem.ValueChanged += TargetBindableItem_ValueSourceChangedListeners;
                targetBindableItem.DefaultAccess = AccessRights.ReadFromPLC;
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem AngleBindableItem
        {
            get { return angleBindableItem; }
            private set
            {
                angleBindableItem = value;
                angleBindableItem.Value = GetAngleToResolution();
                angleBindableItem.DefaultAccess = AccessRights.WriteToPLC;
            }
        }

        [Browsable(false)]
        [AspectProperty(IsVisible = false)]
        [Angle]
        [XmlIgnore]
        public double DistanceTravelled { get { return motor.DistanceTravelled; } }

        [Browsable(false)]
        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public bool IsSteady { get { return motor.IsSteady; } }

        [Browsable(false)]
        [AspectProperty(IsVisible = false)]
        [Exportable(false)]
        [DefaultValue(0)]
        public double CurrentSpeed { get { return motor.CurrentSpeed; } }

        [AspectProperty]
        [DefaultValue(90.0)]
        [AngularSpeed]
        public double Speed
        {
            get { return motor.Speed; }
            set
            {
                if (motor.Speed != value)
                {
                    motor.Speed = value;
                    RaisePropertyChanged(nameof(Speed));

                    if (targetEnabled)
                    {
                        DriveToTarget();
                    }
                }
            }
        }

        [AspectProperty]
        [DefaultValue(0.0)]
        [AngularAcceleration]
        public double Acceleration
        {
            get { return motor.Acceleration; }
            set
            {
                if (motor.Acceleration != value)
                {
                    motor.Acceleration = value;
                    RaisePropertyChanged(nameof(Acceleration));

                    if (targetEnabled)
                    {
                        DriveToTarget();
                    }
                }
            }
        }

        [AspectProperty]
        [DefaultValue(0.0)]
        [AngularAcceleration]
        public double Deceleration
        {
            get { return motor.Deceleration; }
            set
            {
                if (motor.Deceleration != value)
                {
                    motor.Deceleration = value;
                    RaisePropertyChanged(nameof(Deceleration));

                    if (targetEnabled)
                    {
                        DriveToTarget();
                    }
                }
            }
        }

        [AspectProperty]
        [AspectEditor(IsVisiblePropertyLink = nameof(TargetNotEnabled), IsEnabledPropertyLink = nameof(TargetNotEnabled))]
        [DefaultValue(MotorDirection.Forwards)]
        public MotorDirection Direction
        {
            get { return motor.Direction; }
            set
            {
                if (motor.Direction != value)
                {
                    motor.Direction = value;
                    RaisePropertyChanged(nameof(Direction));
                }
            }
        }

        [AspectProperty]
        [AspectEditor(IsVisiblePropertyLink = nameof(TargetNotEnabled), IsEnabledPropertyLink = nameof(TargetNotEnabled))]
        [DefaultValue(MotorState.On)]
        [Exportable(false)]
        public MotorState State
        {
            get { return motor.State; }
            set
            {
                if (motor.State != value)
                {
                    motor.State = value;
                    RaisePropertyChanged(nameof(State));
                }
            }
        }

        [AspectProperty]
        [AspectEditor(IsVisiblePropertyLink = nameof(TargetNotEnabled), IsEnabledPropertyLink = nameof(TargetNotEnabled))]
        [DefaultValue(MotorState.On)]
        public MotorState InitialState
        {
            get { return motor.InitialState; }
            set
            {
                if (motor.InitialState != value)
                {
                    motor.InitialState = value;
                    RaisePropertyChanged(nameof(InitialState));
                }
            }
        }

        [Browsable(false)]
        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public bool Activate
        {
            get
            {
                if (Visual != null && document.Time > 0)
                {
                    return State == MotorState.On && Direction == MotorDirection.Forwards;
                }
                else
                {
                    return InitialState == MotorState.On && Direction == MotorDirection.Forwards;
                }
            }
            set
            {
                if (value)
                {
                    Direction = MotorDirection.Forwards;
                    State = MotorState.On;
                }
                else
                {
                    Direction = MotorDirection.Reverse;
                    State = MotorState.On;
                }
            }
        }

        protected override void OnAssigned()
        {
            base.OnAssigned();

            motor.AttachToVisual(Visual);
            CleanupBindingAPI();

            if (Visual != null)
            {
                CreateBindings();
                UpdateBindings();
            }
        }

        protected override void OnAdded()
        {
            base.OnAdded();
            motor.Initialize();
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();
            motor.OnInitialize();

            UpdateAngleNotifiers();

            if (targetEnabled)
            {
                DriveToTarget();
            }
        }

        protected override void OnReset()
        {
            base.OnReset();
            motor.Reset();

            UpdateAngleNotifiers();
        }

        protected override void OnRemoved()
        {
            foreach (var bindableItem in ((IBindableItemOwner)this).BindableItems)
            {
                bindableItem?.DetachFromVisual();
            }

            motor.Release();

            base.OnRemoved();
        }

        private void CreateBindings()
        {
            if (Visual == null) { return; }

            motor.CreateBindings();

            ActivateBindableItem = new ReadFromServer<bool>(Visual, BindingName(nameof(Activate)));
            TargetBindableItem = new ReadFromServer<double>(Visual, BindingName(nameof(Target)));
            AngleBindableItem = new WriteToServer<double>(Visual, BindingName(nameof(Angle)));
        }

        private void UpdateBindings()
        {
            foreach (var bindableItem in ((IBindableItemOwner)this).BindableItems)
            {
                bindableItem.IsBindingInterface = TriStateYNM.No;
            }

            if (Visual == null) { return; }

            switch (ControlMode)
            {
                case AngularMotorControlMode.OnOff:
                    StateBindableItem.IsBindingInterface = TriStateYNM.Yes;
                    break;
                case AngularMotorControlMode.ForwardReverse:
                    OnForwardsBindableItem.IsBindingInterface = TriStateYNM.Yes;
                    OnReverseBindableItem.IsBindingInterface = TriStateYNM.Yes;
                    break;
                case AngularMotorControlMode.Activate:
                    ActivateBindableItem.IsBindingInterface = TriStateYNM.Yes;
                    break;
                case AngularMotorControlMode.Target:
                    TargetBindableItem.IsBindingInterface = TriStateYNM.Yes;
                    break;
            }

            if (AngleFeedback)
            {
                AngleBindableItem.IsBindingInterface = TriStateYNM.Yes;
            }

            UpdateBindingAPI();
        }

        public object AddDistanceNotifier(double distance, Visual visual)
        {
            return motor.AddDistanceNotifier(distance, visual);
        }

        public void RemoveDistanceNotifier(object handle)
        {
            motor.RemoveDistanceNotifier(handle);
        }

        private void UpdateAngleNotifiers()
        {
            RemoveAngleNotifiers();
            AddAngleNotifiers();
        }

        private double GetAngleToResolution()
        {
            return GetAngleToResolution(DistanceTravelled);
        }

        private double GetAngleToResolution(double angle)
        {
            // Which direction do we round at the boundary?
            // If we are at the boundary between the current value and the lower value then round down.
            // If we are at the boundary between the current value and the upper value then round up.
            // Otherwise, use the usual rounding.
            if (angle == angleLowerBoundary)
            {
                return RoundAngleToResolution(angleLowerBoundary - angleResolution * 0.5);
            }
            else if (angle == angleUpperBoundary)
            {
                return RoundAngleToResolution(angleUpperBoundary + angleResolution * 0.5);
            }

            return RoundAngleToResolution(angle);
        }

        private double RoundAngleToResolution(double angle)
        {
            return Math.Round(angle / angleResolution, MidpointRounding.AwayFromZero) * angleResolution;
        }

        private void AddAngleNotifiers()
        {
            AddAngleNotifiers(GetAngleToResolution());
        }

        private void AddAngleNotifiers(double currentAngle)
        {
            if (IsInitialized && AngleFeedback)
            {
                switch (Direction)
                {
                    case MotorDirection.Forwards:
                        angleUpperBoundary = currentAngle + angleResolution * 0.5;
                        angleUpperBoundaryNotifier = AddDistanceNotifier(angleUpperBoundary, null);
                        break;
                    case MotorDirection.Reverse:
                        angleLowerBoundary = currentAngle - angleResolution * 0.5;
                        angleLowerBoundaryNotifier = AddDistanceNotifier(angleLowerBoundary, null);
                        break;
                }

                OnNotifyDistanceListeners -= MotorAspect_OnNotifyDistance;
                OnNotifyDistanceListeners += MotorAspect_OnNotifyDistance;

                DirectionListeners -= MotorAspect_DirectionListener;
                DirectionListeners += MotorAspect_DirectionListener;
            }

            UpdateAngle(currentAngle);
        }

        private void RemoveAngleNotifiers()
        {
            DirectionListeners -= MotorAspect_DirectionListener;
            OnNotifyDistanceListeners -= MotorAspect_OnNotifyDistance;

            angleUpperBoundary = double.PositiveInfinity;
            angleLowerBoundary = double.NegativeInfinity;

            if (angleUpperBoundaryNotifier != null)
            {
                RemoveDistanceNotifier(angleUpperBoundaryNotifier);
                angleUpperBoundaryNotifier = null;
            }

            if (angleLowerBoundaryNotifier != null)
            {
                RemoveDistanceNotifier(angleLowerBoundaryNotifier);
                angleLowerBoundaryNotifier = null;
            }
        }

        private void MotorAspect_OnNotifyDistance(Visual sender, NotifyDistanceInfo info)
        {
            if (angleUpperBoundaryNotifier != null)
            {
                RemoveDistanceNotifier(angleUpperBoundaryNotifier);
                angleUpperBoundaryNotifier = null;
            }

            if (angleLowerBoundaryNotifier != null)
            {
                RemoveDistanceNotifier(angleLowerBoundaryNotifier);
                angleLowerBoundaryNotifier = null;
            }

            // Update the angle before we change the boundaries, so that we round accordingly.
            var angle = UpdateAngle(info.Distance);

            var upper = angle + angleResolution * 0.5;
            if (upper == info.Distance) { upper += 1e-8; }

            var lower = angle - angleResolution * 0.5;
            if (lower == info.Distance) { lower -= 1e-8; }

            switch (Direction)
            {
                case MotorDirection.Forwards:
                    angleUpperBoundary = upper;
                    angleUpperBoundaryNotifier = AddDistanceNotifier(upper, null);
                    break;
                case MotorDirection.Reverse:
                    angleLowerBoundary = lower;
                    angleLowerBoundaryNotifier = AddDistanceNotifier(lower, null);
                    break;
            }
        }

        private void MotorAspect_DirectionListener(object sender, MotorDirection oldValue, MotorDirection newValue)
        {
            UpdateAngleNotifiers();
        }

        private double UpdateAngle(double value)
        {
            if (AngleFeedback && AngleBindableItem != null)
            {
                var angle = GetAngleToResolution(value);
                AngleBindableItem.Value = angle;
                RaisePropertyChanged(nameof(Angle));
                return angle;
            }

            RaisePropertyChanged(nameof(Angle));
            return Angle;
        }

        private void SetTargetAndNotify(double newTarget)
        {
            if (target != newTarget)
            {
                target = newTarget;

                if (targetEnabled)
                {
                    DriveToTarget();
                }

                RaisePropertyChanged(nameof(Target));
            }
        }

        private void DriveToTarget()
        {
            UpdateOrScheduleEvents();
        }

        void ActivateBindableItem_ValueSourceChangedListeners(BindableItem item)
        {
            Activate = item.ValueAs<bool>();
        }

        void TargetBindableItem_ValueSourceChangedListeners(BindableItem item)
        {
            SetTargetAndNotify(item.ValueAs<double>());
        }

        #region Target Events
        private void SetState(MotorState state)
        {
            if (motor.State != state)
            {
                motor.State = state;
                RaisePropertyChanged(nameof(State));
            }
        }

        private void SetDirection(MotorDirection direction)
        {
            if (motor.Direction != direction)
            {
                motor.Direction = direction;
                RaisePropertyChanged(nameof(Direction));
            }
        }

        private void CancelEvents()
        {
            foreach (var ev in events)
            {
                ev?.Cancel();
            }

            events.Clear();
        }

        private void ScheduleMotorStateEvent(double timeFromNow, MotorState newState)
        {
            var eventQueue = document.EventQueue;
            events.Add(eventQueue.AddNotifier(timeFromNow, () => SetState(newState)));
        }

        private void ScheduleMotorDirectionEvent(double timeFromNow, MotorDirection newDirection)
        {
            var eventQueue = document.EventQueue;
            events.Add(eventQueue.AddNotifier(timeFromNow, () => SetDirection(newDirection)));
        }

        private void UpdateOrScheduleEvents()
        {
            CancelEvents();

            if (Visual == null) { return; }

            double relativeAngle = 0;
            switch (targetType)
            {
                case AngularMotorTargetType.Discontinuous:
                    relativeAngle = target - DistanceTravelled;
                    break;
                case AngularMotorTargetType.Continuous:
                    relativeAngle = KJEUtil.NormalizePlusMinusOneEighty(target - DistanceTravelled);
                    break;
            }

            var motionDirection = (Direction == MotorDirection.Forwards) ? MotionDirection.Forwards : MotionDirection.Reverse;
            var motionProfile = new MotionProfile(motionDirection, Math.Abs(CurrentSpeed), Math.Abs(Speed), Math.Abs(Acceleration), Math.Abs(Deceleration));
            var motionEvents = new List<MotionEvent>();
            var motionTime = MotionSolver.Time(relativeAngle, motionProfile, motionEvents);
            if (motionTime < 0 || Double.IsInfinity(motionTime) || Double.IsNaN(motionTime))
            {
                app.LogMessage("Warning", "Motion is unsolvable", Visual);
            }

            var direction = Direction;
            foreach (var motionEvent in motionEvents)
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
                        direction = MotorDirection.Forwards;
                        ScheduleMotorDirectionEvent(motionEvent.Time, direction);
                        break;
                    case MotionEventType.ChangeDirectionReverse:
                        direction = MotorDirection.Reverse;
                        ScheduleMotorDirectionEvent(motionEvent.Time, direction);
                        break;
                }
            }
        }
        #endregion
    }
}
