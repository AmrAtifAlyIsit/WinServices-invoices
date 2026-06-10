namespace EthraiOrderFixService;
public class WorkerOptions
{
    // RunRecurring: true => run every IntervalMinutes; false => run once at startup
    public bool RunRecurring { get; set; } = true;
    public int IntervalMinutes { get; set; } = 4;
    public string ServiceName { get; set; } = "EthraiOrderFixService";
}
