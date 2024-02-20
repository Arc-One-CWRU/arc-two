using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Xml.Serialization;

using Demo3D.Common;
using Demo3D.Visuals;
using Demo3D.Visuals.Renderers;
using Demo3D.PLC;
using Demo3D.PLC.Comms;
using Demo3D.Gui.AspectViewer;
using Demo3D.Gui.AspectViewer.Editors;

namespace Demo3D.Components {
    
    using Properties;

    public enum IndicatorLightControlMode
    {
        None = 0,
        OnOff = 1,
    }

    [Resources(typeof(Resources))]
    [Category(nameof(Resources.ControlPanels_Category))]
    [HelpUrl("indicator_light")]
    public class IndicatorLight : ExportableVisualAspect, IBindableItemOwner
    {
        [Flags]
        public enum Input
        {
            None = 0,
            State = 1 << 0
        }

        private Input inputs = Input.None;
        private BindableItem<bool> isLampOnBindableItem;

        [DefaultValue(IndicatorLightControlMode.None)]
        public Input Inputs
        {
            get { return inputs; }
            set
            {
                if (SetProperty(ref inputs, value))
                {
                    UpdateBindings();
                }
            }
        }

        [AspectProperty]
        [XmlIgnore]
        public bool IsLampOn
        {
            get { return isLampOnBindableItem?.ValueAs<bool>() ?? false; }
            set
            {
                if (isLampOnBindableItem != null && isLampOnBindableItem.ValueAs<bool>() != value)
                {
                    isLampOnBindableItem.Value = value;
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem<bool> IsLampOnBindableItem
        {
            get { return isLampOnBindableItem; }
            private set
            {
                if (isLampOnBindableItem != value)
                {
                    if (isLampOnBindableItem != null)
                    {
                        isLampOnBindableItem.ValueChanged -= OnIsLampOnBindableItemChanged;
                        isLampOnBindableItem.DetachFromVisual();
                        isLampOnBindableItem = null;
                    }

                    isLampOnBindableItem = value;
                    if (isLampOnBindableItem != null)
                    {
                        isLampOnBindableItem.ValueChanged += OnIsLampOnBindableItemChanged;
                        isLampOnBindableItem.DefaultAccess = AccessRights.ReadFromPLC;
                    }
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public IEnumerable<BindableItem> BindableItems
        {
            get
            {
                if (IsLampOnBindableItem != null) { yield return IsLampOnBindableItem; }
            }
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

        protected override void OnAdded()
        {
            base.OnAdded();

            UpdateLuminosity(IsLampOn);
        }

        protected void UpdateLuminosity(bool lampOn)
        {
            if (Visual == null) { return; }

            var luminosity = (lampOn) ? 1.0 : 0.0;
            var materialContainers = Visual.FindVisualAndDescendantsAspects<IMaterialContainerAspect>();
            foreach (var materialContainer in materialContainers)
            {
                foreach (var material in materialContainer.Materials)
                {
                    material.Luminosity = luminosity;
                }
            }
        }

        private void OnIsLampOnBindableItemChanged(BindableItem obj)
        {
            UpdateLuminosity(obj?.ValueAs<bool>() ?? false);
            RaisePropertyChanged(nameof(IsLampOn));
        }

        private void CreateBindings()
        {
            IsLampOnBindableItem = new ReadFromServer<bool>(Visual, BindingName(nameof(IsLampOn)));
            UpdateBindings();
        }

        private void RemoveBindings()
        {
            IsLampOnBindableItem = null;

            ReleaseBindingName(nameof(IsLampOn));

            UpdateBindingAPI();
        }

        private void UpdateBindings()
        {
            if (IsLampOnBindableItem != null) { IsLampOnBindableItem.IsBindingInterface = inputs.HasFlag(Input.State) ? TriStateYNM.Yes : TriStateYNM.No; Visual.App.LogMessage("Info", "EnableBinding", null); }

            UpdateBindingAPI();
        }
    }
}
