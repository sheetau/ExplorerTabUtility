using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ExplorerTabUtility.Interop;

public record struct ScreenBounds(double X, double Y, double Width, double Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(double x, double y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
}

// Only the read-only UI Automation methods used by taskbar discovery are exposed.
// Vtable slots follow the Windows SDK UIAutomationClient.h interfaces.
internal sealed class NativeAutomation : IDisposable
{
    private readonly IUIAutomation _automation = (IUIAutomation)Activator.CreateInstance(
        Type.GetTypeFromCLSID(new Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E"))!)!;
    public NativeElement FromHandle(nint window)
    {
        _automation.ElementFromHandle(window, out var element);
        return new NativeElement(_automation, element);
    }
    public void Dispose() => Marshal.ReleaseComObject(_automation);
}

internal sealed class NativeElement(IUIAutomation automation, IUIAutomationElement element) : IDisposable
{
    private object Property(int id) { element.GetCurrentPropertyValue(id, out var value); return value; }
    public string Name => Property(30005) as string ?? "";
    public string ClassName => Property(30012) as string ?? "";
    public int ControlType => Property(30003) is int type ? type : 0;
    public bool IsEnabled => Property(30010) is true;
    public bool IsOffscreen => Property(30022) is true;
    public bool CanInvoke => Property(30031) is true;
    public ScreenBounds Bounds => Property(30001) is double[] { Length: 4 } bounds
        ? new(bounds[0], bounds[1], bounds[2], bounds[3]) : default;
    public NativeElement? ChildById(string id)
    {
        automation.CreatePropertyCondition(30011, id, out var condition);
        try
        {
            element.FindFirst(2, condition, out var child);
            return child == null ? null : new NativeElement(automation, child);
        }
        finally { Marshal.ReleaseComObject(condition); }
    }
    public IEnumerable<NativeElement> FindAll(int scope, int controlType)
    {
        automation.CreatePropertyCondition(30003, controlType, out var condition);
        IUIAutomationElementArray? items = null;
        try
        {
            element.FindAll(scope, condition, out items);
            items.GetLength(out var count);
            for (var i = 0; i < count; i++)
            {
                items.GetElement(i, out var child);
                yield return new NativeElement(automation, child);
            }
        }
        finally
        {
            if (items != null) Marshal.ReleaseComObject(items);
            Marshal.ReleaseComObject(condition);
        }
    }
    public void Dispose() => Marshal.ReleaseComObject(element);
}

[ComImport, Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation
{
    void _VtblGap1_3();
    void ElementFromHandle(nint window, out IUIAutomationElement element);
    void _VtblGap2_16();
    void CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value, out IUIAutomationCondition condition);
}

[ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    void _VtblGap1_2();
    void FindFirst(int scope, IUIAutomationCondition condition, out IUIAutomationElement? element);
    void FindAll(int scope, IUIAutomationCondition condition, out IUIAutomationElementArray elements);
    void _VtblGap2_3();
    void GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
}

[ComImport, Guid("14314595-B4BC-4055-95F2-58F2E42C9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElementArray
{
    void GetLength(out int length);
    void GetElement(int index, out IUIAutomationElement element);
}

[ComImport, Guid("352FFBA8-0973-437C-A61F-F64CAFD81DF9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationCondition { }
