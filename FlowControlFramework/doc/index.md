## About

Framework script for the [Emulate3D.FlowControl](/packages/pkg_emulate3d_flowcontrol) package.

---

## Uses

Provides native objects, and an API for controlling the routing of loads through a system. Referenced by the [Emulate3D.FlowControl](/packages/pkg_emulate3d_flowcontrol) package, and others.

---

## Documentation

See [Emulate3D.FlowControl](/packages/pkg_emulate3d_flowcontrol).

---

## Tutorials

See [Emulate3D.FlowControl](/packages/pkg_emulate3d_flowcontrol).

---

## Training Videos

See [Emulate3D.FlowControl](/packages/pkg_emulate3d_flowcontrol).

---

## Revision History

**2.0.0**
- Support for net6.

**1.0.5**
- Fix issue where a load was not removed from the RoutingTargetAllocators custom property on the target on deletion.

**1.0.4**
- Set load local x location to zero when warped to a station.

**1.0.3**
- Set load rotation to match target station rotation in "warp load {load} to station {station}" widget.
- Handle VisualReferences when retrieving an order list from an object.

**1.0.2**
- Make Name property simple on controller visuals.
- Fix issue where wrong label was used when upgrading legacy controller.
- Fix NullReferenceException when deleting load with no associated station/controller.
- Fix issue where legacy components would not be upgraded.

**1.0.1**
- Prevent upgrade of legacy components if script name or script reference modifications are detected.

**1.0.0**
- Initial version.
