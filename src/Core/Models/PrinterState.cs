namespace PrusaConnect.Core.Models;

public enum PrinterState
{
    Unknown,
    Offline,
    Idle,
    Busy,
    Ready,
    Printing,
    Paused,
    Finished,
    Stopped,
    Error,
    Attention,
}
