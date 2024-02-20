using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;

using Demo3D.PLC;
using Demo3D.PLC.Comms;
using Demo3D.EventQueue;
using Demo3D.Gui.AspectViewer;
using Demo3D.Gui.AspectViewer.Editors;
using Demo3D.Common;
using Demo3D.Utilities;
using Demo3D.Visuals;
using Demo3D.Visuals.Motor;

namespace Demo3D.Components
{
    using Properties;

    [Resources(typeof(Resources))]
    [Category(nameof(Resources.Sensors_Category))]
    [HelpUrl("limit_sensor")]
    public class LimitSensor : ExportableVisualAspect, IBindableItemOwner
    {
        [Flags]
        public enum Output
        {
            None = 0,
            Lower = 1 << 0,
            Upper = 1 << 1,
            Both = Lower | Upper
        }

        private Output outputs;
        private IMotor motor;
        private double frequency;
        private double tolerance;

        private bool useMotorLimits;

        private double lowerLimit;
        private double upperLimit;

        private bool atLowerLimit;
        private bool atUpperLimit;

        private BindableItem atLowerLimitBindableItem;
        private BindableItem atUpperLimitBindableItem;

        private Event scheduledEvent;

        [AspectProperty]
        [DefaultValue(Output.Both)]
        public Output Outputs
        {
            get { return outputs; }
            set
            {
                if (SetProperty(ref outputs, value))
                {
                    UpdateBindings();

                    RaisePropertyChanged(nameof(LowerLimitEnabled));
                    RaisePropertyChanged(nameof(UpperLimitEnabled));
                    RaisePropertyChanged(nameof(ManualLowerLimit));
                    RaisePropertyChanged(nameof(ManualUpperLimit));
                }
            }
        }

        [AspectProperty]
        [Required]
        public IMotor Motor
        {
            get { return motor; }
            set
            {
                Unsubscribe();

                if (SetProperty(ref motor, value))
                {
                    RaisePropertyChanged(nameof(Units));
                }

                Subscribe();
            }
        }

        [AspectProperty]
        [Frequency]
        [DefaultValue(10.0)]
        public double Frequency
        {
            get { return frequency; }
            set
            {
                var clampedValue = Math.Min(100.0, Math.Max(0.0, value));
                if (SetProperty(ref frequency, clampedValue))
                {
                    ReschedulePolling();

                    if (clampedValue != value)
                    {
                        RaisePropertyChanged(nameof(Frequency));
                    }
                }
            }
        }

        [AspectProperty]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(Units))]
        [DefaultValue(1e-3)]
        public double Tolerance
        {
            get { return tolerance; }
            set
            {
                var clampedValue = Math.Max(0.0, value);
                if (SetProperty(ref tolerance, clampedValue))
                {
                    if (clampedValue != value)
                    {
                        RaisePropertyChanged(nameof(Tolerance));
                    }
                }
            }
        }

        [AspectProperty]
        [DefaultValue(true)]
        public bool UseMotorLimits
        {
            get { return useMotorLimits; }
            set
            {
                if (SetProperty(ref useMotorLimits, value))
                {
                    RaisePropertyChanged(nameof(ManualLowerLimit));
                    RaisePropertyChanged(nameof(ManualUpperLimit));
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool LowerLimitEnabled
        {
            get { return outputs.HasFlag(Output.Lower); }
        }

        [AspectProperty(IsVisible = false)]
        public bool UpperLimitEnabled
        {
            get { return outputs.HasFlag(Output.Upper); }
        }

        [AspectProperty(IsVisible = false)]
        public bool ManualLowerLimit
        {
            get { return LowerLimitEnabled && useMotorLimits == false; }
        }

        [AspectProperty(IsVisible = false)]
        public bool ManualUpperLimit
        {
            get { return UpperLimitEnabled && useMotorLimits == false; }
        }

        [AspectProperty]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(Units), IsVisiblePropertyLink = nameof(ManualLowerLimit), IsEnabledPropertyLink = nameof(ManualLowerLimit))]
        [DefaultValue(0.0)]
        public double LowerLimit
        {
            get { return lowerLimit; }
            set { SetProperty(ref lowerLimit, value); }
        }

        [AspectProperty]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(Units), IsVisiblePropertyLink = nameof(ManualUpperLimit), IsEnabledPropertyLink = nameof(ManualUpperLimit))]
        [DefaultValue(0.0)]
        public double UpperLimit
        {
            get { return upperLimit; }
            set { SetProperty(ref upperLimit, value); }
        }

        [AspectProperty]
        [AspectEditor(IsVisiblePropertyLink = nameof(LowerLimitEnabled), IsEnabledPropertyLink = nameof(LowerLimitEnabled))]
        public bool AtLowerLimit
        {
            get { return atLowerLimit; }
            private set
            {
                if (SetProperty(ref atLowerLimit, value))
                {
                    if (AtLowerLimitBindableItem != null)
                    {
                        AtLowerLimitBindableItem.Value = atLowerLimit;
                    }
                }
            }
        }

        [AspectProperty]
        [AspectEditor(IsVisiblePropertyLink = nameof(UpperLimitEnabled), IsEnabledPropertyLink = nameof(UpperLimitEnabled))]
        public bool AtUpperLimit
        {
            get { return atUpperLimit; }
            private set
            {
                if (SetProperty(ref atUpperLimit, value))
                {
                    if (AtUpperLimitBindableItem != null)
                    {
                        AtUpperLimitBindableItem.Value = atUpperLimit;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public UnitsAttribute Units
        {
            get { return (motor is MotorAspect motorAspect) ? motorAspect.PositionUnits : null; }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem AtLowerLimitBindableItem
        {
            get { return atLowerLimitBindableItem; }
            private set
            {
                if (atLowerLimitBindableItem != value)
                {
                    atLowerLimitBindableItem?.DetachFromVisual();
                    atLowerLimitBindableItem = value;

                    if (atLowerLimitBindableItem != null)
                    {
                        atLowerLimitBindableItem.Value = AtLowerLimit;
                        atLowerLimitBindableItem.DefaultAccess = AccessRights.WriteToPLC;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem AtUpperLimitBindableItem
        {
            get { return atUpperLimitBindableItem; }
            private set
            {
                if (atUpperLimitBindableItem != value)
                {
                    atUpperLimitBindableItem?.DetachFromVisual();
                    atUpperLimitBindableItem = value;

                    if (atUpperLimitBindableItem != null)
                    {
                        atUpperLimitBindableItem.Value = AtUpperLimit;
                        atUpperLimitBindableItem.DefaultAccess = AccessRights.WriteToPLC;
                    }
                }
            }
        }

        IEnumerable<BindableItem> IBindableItemOwner.BindableItems
        {
            get
            {
                if (AtLowerLimitBindableItem != null) yield return AtLowerLimitBindableItem;
                if (AtUpperLimitBindableItem != null) yield return AtUpperLimitBindableItem;
            }
        }

        public LimitSensor()
        {
            outputs = Output.Both;
            motor = null;
            frequency = 10.0;
            tolerance = 1e-3;
            useMotorLimits = true;
            lowerLimit = 0.0;
            upperLimit = 0.0;
            atLowerLimit = false;
            atUpperLimit = false;
            atLowerLimitBindableItem = null;
            atUpperLimitBindableItem = null;
            scheduledEvent = null;
        }

        private void ReschedulePolling()
        {
            if (Visual != null)
            {
                CancelPolling();
                if (frequency > 0.0)
                {
                    PollAndSchedule();
                }
            }
        }

        private void PollAndSchedule()
        {
            Poll();

            double seconds = 1.0F / frequency;
            var secondsAsFixed = Fixed.RoundUp(ref seconds);
            scheduledEvent = Visual.Document.EventQueue.ScheduleAction(secondsAsFixed, PollAndSchedule);
        }

        private void CancelPolling()
        {
            scheduledEvent?.Cancel();
            scheduledEvent = null;
        }

        private void UpdateAtLimits()
        {
            if (useMotorLimits)
            {
                if (motor is MotorAspect motorAspect)
                {
                    var position = motorAspect.Current().Position;
                    AtLowerLimit = position <= (motorAspect.LowerPositionLimit + tolerance);
                    AtUpperLimit = position >= (motorAspect.UpperPositionLimit - tolerance);
                }
            }
            else
            {
                var position = motor.DistanceTravelled;
                AtLowerLimit = position <= (lowerLimit + tolerance);
                AtUpperLimit = position >= (upperLimit - tolerance);
            }
        }

        private void Poll()
        {
            UpdateAtLimits();
        }

        private void Subscribe()
        {
            if (motor is MotorAspect motorAspect)
            {
                motorAspect.OnAfterReset += OnAfterReset;
                motorAspect.OnUnitsChanged += OnUnitsChanged;
            }
        }

        private void Unsubscribe()
        {
            if (motor is MotorAspect motorAspect)
            {
                motorAspect.OnAfterReset -= OnAfterReset;
                motorAspect.OnUnitsChanged -= OnUnitsChanged;
            }
        }

        private void OnAfterReset(MotorAspect motorAspect)
        {
            UpdateAtLimits(); // Initial state may have been applied.
        }

        private void OnUnitsChanged(MotorAspect motor, MotorAspect.Units oldUnits, MotorAspect.Units newUnits)
        {
            RaisePropertyChanged(nameof(Units));
        }

        private void CreateBindings()
        {
            if (Visual == null) { return; }

            AtLowerLimitBindableItem = new WriteToServer<bool>(Visual, BindingName(nameof(AtLowerLimit)));
            AtUpperLimitBindableItem = new WriteToServer<bool>(Visual, BindingName(nameof(AtUpperLimit)));
        }

        private void UpdateBindings()
        {
            foreach (var bindableItem in ((IBindableItemOwner)this).BindableItems)
            {
                bindableItem.IsBindingInterface = TriStateYNM.No;
            }

            if (Visual == null) { return; }

            if (Outputs.HasFlag(Output.Lower))
            {
                AtLowerLimitBindableItem.IsBindingInterface = TriStateYNM.Yes;
            }

            if (Outputs.HasFlag(Output.Upper))
            {
                AtUpperLimitBindableItem.IsBindingInterface = TriStateYNM.Yes;
            }

            UpdateBindingAPI();
        }

        protected override void OnAssigned()
        {
            base.OnAssigned();

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

            UpdateAtLimits();
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();

            UpdateAtLimits();
            ReschedulePolling();
        }

        protected override void OnReset()
        {
            base.OnReset();

            UpdateAtLimits();
        }

        protected override void OnRemoved()
        {
            CancelPolling();

            base.OnRemoved();
        }
    }
}
