using Avalonia.Input;

namespace TCMPlus.App.Controls;

public static class PatientDragData
{
    public static readonly DataFormat<string> Format = DataFormat.CreateStringApplicationFormat("TCMPlus.Patient");
}
