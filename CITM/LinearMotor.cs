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

namespace Demo3D.Components
{
    using Properties;

    public enum LinearMotorControlMode
    {
        None,
        OnOff,
        ForwardReverse,
        Activate,
        Target
    }

    [Resources(typeof(Resources))]
    [Category(nameof(Resources.Motors_Category))]
    [HelpUrl("linear_motor")]
    [Obsolete]
    public class LinearMotor : ExportableVisualAspect, IMotor, IBindableItemOwner
    {
        private readonly Motor<ConveyorMotorProperties> motor = new Motor<ConveyorMotorProperties>() { Acceleration = 0, Deceleration = 0 };
        private LinearMotorControlMode controlMode = LinearMotorControlMode.None;
        private bool targetEnabled = false;
        private double target = 0;
        private bool positionFeedback = false;
        private double positionResolution = 0.05;
        private double positionLowerBoundary = double.NegativeInfinity;
        private double positionUpperBoundary = double.PositiveInfinity;
        private object positionLowerBoundaryNotifier;
        private object positionUpperBoundaryNotifier;
        private BindableItem targetBindableItem;
        private BindableItem positionBindableItem;
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
        [DefaultValue(LinearMotorControlMode.None)]
        public LinearMotorControlMode ControlMode
        {
            get { return controlMode; }
            set
            {
                if (SetProperty(ref controlMode, value))
                {
                    // Automatically set target enabled if the control mode is set to target.
                    if (controlMode == LinearMotorControlMode.Target)
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
            get { return ControlMode != LinearMotorControlMode.Target; }
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
                if (SetProperty(ref targetEnabled, (ControlMode != LinearMotorControlMode.Target) ? value : true))
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
        [Distance]
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
        [DefaultValue(false)]
        [Cat("Feedback")]
        public bool PositionFeedback
        {
            get { return positionFeedback; }

            set
            {
                if (SetProperty(ref positionFeedback, value))
                {
                    UpdatePositionNotifiers();
                    UpdateBindings();
                }
            }
        }

        [AspectProperty]
        [AspectEditor(IsVisiblePropertyLink = nameof(PositionFeedback), IsEnabledPropertyLink = nameof(PositionFeedback))]
        [Distance]
        [DefaultValue(0.05)]
        [Cat("Feedback")]
        public double PositionResolution
        {
            get { return positionResolution; }
            
            set
            {
                if (SetProperty(ref positionResolution, Math.Max(value, 0.001)))
                {
                    UpdatePositionNotifiers();
                }
            }
        }

        [XmlIgnore]
        [AspectProperty]
        [AspectEditor(IsVisiblePropertyLink = nameof(PositionFeedback), IsEnabledPropertyLink = nameof(PositionFeedback))]
        [Distance]
        [Cat("Feedback")]
        public double Position
        {
            get
            {
                if (PositionFeedback && PositionBindableItem != null)
                {
                    return PositionBindableItem.ValueAs<double>();
                }

                return DistanceTravelled;
            }
        }

        [AspectProperty]
        [Distance]
        [DefaultValue(0)]
        public double InitialPosition
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
                if (PositionBindableItem != null) yield return PositionBindableItem;
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
        public BindableItem PositionBindableItem
        {
            get { return positionBindableItem; }
            private set
            {
                positionBindableItem = value;
                positionBindableItem.Value = GetPositionToResolution();
                positionBindableItem.DefaultAccess = AccessRights.WriteToPLC;
            }
        }

        [Browsable(false)]
        [AspectProperty(IsVisible = false)]
        [Distance]
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
        [DefaultValue(0.5)]
        [Speed]
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
        [Acceleration]
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
        [Acceleration]
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

            UpdatePositionNotifiers();

            if (targetEnabled)
            {
                DriveToTarget();
            }
        }

        protected override void OnReset()
        {
            base.OnReset();
            motor.Reset();

            UpdatePositionNotifiers();
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
            PositionBindableItem = new WriteToServer<double>(Visual, BindingName(nameof(Position)));
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
                case LinearMotorControlMode.OnOff:
                    StateBindableItem.IsBindingInterface = TriStateYNM.Yes;
                    break;
                case LinearMotorControlMode.ForwardReverse:
                    OnForwardsBindableItem.IsBindingInterface = TriStateYNM.Yes;
                    OnReverseBindableItem.IsBindingInterface = TriStateYNM.Yes;
                    break;
                case LinearMotorControlMode.Activate:
                    ActivateBindableItem.IsBindingInterface = TriStateYNM.Yes;
                    break;
                case LinearMotorControlMode.Target:
                    TargetBindableItem.IsBindingInterface = TriStateYNM.Yes;
                    break;
            }

            if (PositionFeedback)
            {
                PositionBindableItem.IsBindingInterface = TriStateYNM.Yes;
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

        private void UpdatePositionNotifiers()
        {
            RemovePositionNotifiers();
            AddPositionNotifiers();
        }

        private double GetPositionToResolution(double position)
        {
            // Which direction do we round at the boundary?
            // If we are at the boundary between the current value and the lower value then round down.
            // If we are at the boundary between the current value and the upper value then round up.
            // Otherwise, use the usual rounding.
            if (position == positionLowerBoundary)
            {
                return RoundPositionToResolution(positionLowerBoundary - positionResolution * 0.5);
            }
            else if (position == positionUpperBoundary)
            {
                return RoundPositionToResolution(positionUpperBoundary + positionResolution * 0.5);
            }

            return RoundPositionToResolution(position);
        }

        private double RoundPositionToResolution(double position)
        {
            return Math.Round(position / positionResolution, MidpointRounding.AwayFromZero) * positionResolution;
        }

        private double GetPositionToResolution()
        {
            return GetPositionToResolution(DistanceTravelled);
        }

        private void AddPositionNotifiers()
        {
            AddPositionNotifiers(GetPositionToResolution());
        }

        private void AddPositionNotifiers(double currentPosition)
        {
            if (IsInitialized && PositionFeedback)
            {
                switch (Direction)
                {
                    case MotorDirection.Forwards:
                        positionUpperBoundary = currentPosition + positionResolution * 0.5;
                        positionUpperBoundaryNotifier = AddDistanceNotifier(positionUpperBoundary, null);
                        break;
                    case MotorDirection.Reverse:
                        positionLowerBoundary = currentPosition - positionResolution * 0.5;
                        positionLowerBoundaryNotifier = AddDistanceNotifier(positionLowerBoundary, null);
                        break;
                }

                OnNotifyDistanceListeners -= MotorAspect_OnNotifyDistance;
                OnNotifyDistanceListeners += MotorAspect_OnNotifyDistance;

                DirectionListeners -= MotorAspect_DirectionListener;
                DirectionListeners += MotorAspect_DirectionListener;
            }

            UpdatePosition(currentPosition);
        }

        private void RemovePositionNotifiers()
        {
            DirectionListeners -= MotorAspect_DirectionListener;
            OnNotifyDistanceListeners -= MotorAspect_OnNotifyDistance;

            positionUpperBoundary = double.PositiveInfinity;
            positionLowerBoundary = double.NegativeInfinity;

            if (positionUpperBoundaryNotifier != null)
            {
                RemoveDistanceNotifier(positionUpperBoundaryNotifier);
                positionUpperBoundaryNotifier = null;
            }

            if (positionLowerBoundaryNotifier != null)
            {
                RemoveDistanceNotifier(positionLowerBoundaryNotifier);
                positionLowerBoundaryNotifier = null;
            }
        }

        private void MotorAspect_DirectionListener(object sender, MotorDirection oldValue, MotorDirection newValue)
        {
            UpdatePositionNotifiers();
        }

        private void MotorAspect_OnNotifyDistance(Visual sender, NotifyDistanceInfo info)
        {
            if (positionUpperBoundaryNotifier != null)
            {
                RemoveDistanceNotifier(positionUpperBoundaryNotifier);
                positionUpperBoundaryNotifier = null;
            }

            if (positionLowerBoundaryNotifier != null)
            {
                RemoveDistanceNotifier(positionLowerBoundaryNotifier);
                positionLowerBoundaryNotifier = null;
            }

            // Update the position before we change the boundaries, so that we round accordingly.
            var position = UpdatePosition(info.Distance);

            var upper = position + positionResolution * 0.5;
            if (upper == info.Distance) { upper += 1e-8; }

            var lower = position - positionResolution * 0.5;
            if (lower == info.Distance) { lower -= 1e-8; }

            switch (Direction)
            {
                case MotorDirection.Forwards:
                    positionUpperBoundary = upper;
                    positionUpperBoundaryNotifier = AddDistanceNotifier(upper, null);
                    break;
                case MotorDirection.Reverse:
                    positionLowerBoundary = lower;
                    positionLowerBoundaryNotifier = AddDistanceNotifier(lower, null);
                    break;
            }
        }

        private double UpdatePosition(double value)
        {
            if (PositionFeedback && PositionBindableItem != null)
            {
                var position = GetPositionToResolution(value);
                PositionBindableItem.Value = position;
                RaisePropertyChanged(nameof(Position));
                return position;
            }

            RaisePropertyChanged(nameof(Position));
            return Position;
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

            var distance = target - DistanceTravelled;

            var motionDirection = (Direction == MotorDirection.Forwards) ? MotionDirection.Forwards : MotionDirection.Reverse;
            var motionProfile = new MotionProfile(motionDirection, Math.Abs(CurrentSpeed), Math.Abs(Speed), Math.Abs(Acceleration), Math.Abs(Deceleration));
            var motionEvents = new List<MotionEvent>();
            var motionTime = MotionSolver.Time(distance, motionProfile, motionEvents);
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
