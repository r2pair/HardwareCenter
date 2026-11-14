using System.Globalization;

namespace HardwareCenter.Core;

/// <summary>Turns raw sensor values into the numbers and units people read.</summary>
public static class SensorFormat
{
    public const string Missing = "—";

    /// <summary>Display temperatures in Fahrenheit. Raw values stay in Celsius everywhere else.</summary>
    public static bool UseFahrenheit { get; set; }

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    public static string TemperatureUnit => UseFahrenheit ? "°F" : "°C";

    public static double ToDisplayTemperature(double celsius) => UseFahrenheit ? celsius * 9.0 / 5.0 + 32.0 : celsius;

    /// <summary>Full-precision value and unit for sensor lists, e.g. ("54.5", "°C").</summary>
    public static (string Number, string Unit) Split(SensorKind kind, float value) => kind switch
    {
        SensorKind.Temperature => (ToDisplayTemperature(value).ToString("F1", Culture), TemperatureUnit),
        SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity => (value.ToString("F1", Culture), "%"),
        SensorKind.Clock => value >= 1000 ? ((value / 1000).ToString("F2", Culture), "GHz") : (value.ToString("F0", Culture), "MHz"),
        SensorKind.Power => (value.ToString("F1", Culture), "W"),
        SensorKind.Voltage => (value.ToString("F3", Culture), "V"),
        SensorKind.Current => (value.ToString("F2", Culture), "A"),
        SensorKind.Fan => (value.ToString("F0", Culture), "RPM"),
        SensorKind.Flow => (value.ToString("F1", Culture), "L/h"),
        SensorKind.Frequency => value >= 1000 ? ((value / 1000).ToString("F2", Culture), "kHz") : (value.ToString("F1", Culture), "Hz"),
        SensorKind.Data => Gigabytes(value),
        SensorKind.SmallData => value >= 1024 ? ((value / 1024).ToString("F1", Culture), "GB") : (value.ToString("F0", Culture), "MB"),
        SensorKind.Throughput => Rate(value),
        SensorKind.TimeSpan => (Duration(value), ""),
        SensorKind.Timing => (value.ToString("F1", Culture), "ns"),
        SensorKind.Energy => value >= 1000 ? ((value / 1000).ToString("F1", Culture), "Wh") : (value.ToString("F0", Culture), "mWh"),
        SensorKind.Noise => (value.ToString("F0", Culture), "dBA"),
        SensorKind.Conductivity => (value.ToString("F1", Culture), "µS/cm"),
        _ => (value.ToString(Math.Abs(value) >= 100 ? "N0" : "0.###", Culture), ""),
    };

    /// <summary>Rounded value and unit for headline figures, e.g. ("55", "°C").</summary>
    public static (string Number, string Unit) SplitCompact(SensorKind kind, float value) => kind switch
    {
        SensorKind.Temperature => (ToDisplayTemperature(value).ToString("F0", Culture), TemperatureUnit),
        SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity => (value.ToString("F0", Culture), "%"),
        SensorKind.Power => (value.ToString(value >= 100 ? "F0" : "F1", Culture), "W"),
        _ => Split(kind, value),
    };

    public static string Format(SensorKind kind, float? value)
    {
        if (value is not float v)
            return Missing;

        (string number, string unit) = Split(kind, v);
        return unit.Length == 0 ? number : $"{number} {unit}";
    }

    public static string FormatCompact(SensorKind kind, float? value)
    {
        if (value is not float v)
            return Missing;

        (string number, string unit) = SplitCompact(kind, v);
        return unit.Length == 0 ? number : unit == "%" ? number + unit : $"{number} {unit}";
    }

    /// <summary>Short label for a chart axis tick, e.g. "50%", "60°", "2 MB/s".</summary>
    public static string Axis(SensorKind kind, double value) => kind switch
    {
        SensorKind.Temperature => ToDisplayTemperature(value).ToString("F0", Culture) + "°",
        SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity => value.ToString("F0", Culture) + "%",
        SensorKind.Clock => value >= 1000 ? (value / 1000).ToString("0.#", Culture) + " GHz" : value.ToString("F0", Culture) + " MHz",
        SensorKind.Voltage => value.ToString("0.00", Culture) + " V",
        SensorKind.Throughput => RateAxis(value),
        SensorKind.Fan => value.ToString("F0", Culture),
        SensorKind.Power => value.ToString("0.#", Culture) + " W",
        _ => FormatAxisGeneric(kind, value),
    };

    private static string FormatAxisGeneric(SensorKind kind, double value)
    {
        (string number, string unit) = Split(kind, (float)value);
        return unit.Length == 0 ? number : $"{number} {unit}";
    }

    public static string Bytes(long bytes)
    {
        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string format = unit == 0 || value >= 100 ? "F0" : value >= 10 ? "F1" : "F2";
        return $"{value.ToString(format, Culture)} {units[unit]}";
    }

    /// <summary>Capacity as drive makers label it (decimal units): "2 TB", "500 GB".</summary>
    public static string Capacity(long bytes)
    {
        if (bytes <= 0)
            return "";

        double gb = bytes / 1e9;
        return gb >= 1000 ? $"{(gb / 1000).ToString("0.##", Culture)} TB" : $"{gb.ToString("F0", Culture)} GB";
    }

    public static string LinkSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
            return "";

        double mbps = bitsPerSecond / 1e6;
        return mbps >= 1000 ? $"{(mbps / 1000).ToString("0.##", Culture)} Gbps" : $"{mbps.ToString("0.#", Culture)} Mbps";
    }

    public static string Duration(double seconds)
    {
        TimeSpan span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1)
            return $"{span.Minutes}m {span.Seconds}s";
        return $"{span.Seconds}s";
    }

    public static string GroupTitle(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "Temperatures",
        SensorKind.Load => "Load",
        SensorKind.Fan => "Fans",
        SensorKind.Control => "Fan control",
        SensorKind.Clock => "Clocks",
        SensorKind.Power => "Power",
        SensorKind.Voltage => "Voltages",
        SensorKind.Current => "Currents",
        SensorKind.Data or SensorKind.SmallData => "Data",
        SensorKind.Throughput => "Throughput",
        SensorKind.Level => "Levels",
        SensorKind.Factor => "Counters",
        SensorKind.Frequency => "Frequencies",
        SensorKind.Flow => "Flow",
        SensorKind.TimeSpan => "Time",
        SensorKind.Timing => "Timings",
        SensorKind.Energy => "Energy",
        SensorKind.Noise => "Noise",
        SensorKind.Conductivity => "Conductivity",
        SensorKind.Humidity => "Humidity",
        _ => kind.ToString(),
    };

    /// <summary>Order in which sensor groups are listed: what people look for first comes first.</summary>
    public static int GroupOrder(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => 0,
        SensorKind.Load => 1,
        SensorKind.Fan => 2,
        SensorKind.Control => 3,
        SensorKind.Clock => 4,
        SensorKind.Power => 5,
        SensorKind.Voltage => 6,
        SensorKind.Current => 7,
        SensorKind.Throughput => 8,
        SensorKind.Data => 9,
        SensorKind.SmallData => 10,
        SensorKind.Level => 11,
        _ => 12 + (int)kind,
    };

    /// <summary>Position on a 0..1 meter, or NaN for kinds that have no natural scale.</summary>
    public static double Fraction(SensorKind kind, float value) => kind switch
    {
        SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity => Math.Clamp(value / 100.0, 0, 1),
        SensorKind.Temperature => Math.Clamp(value / 100.0, 0, 1),
        _ => double.NaN,
    };

    private static (string Number, string Unit) Gigabytes(float gb)
    {
        if (gb >= 1024)
            return ((gb / 1024).ToString("F2", Culture), "TB");
        if (gb >= 1 || gb <= 0)
            return (gb.ToString("F1", Culture), "GB");
        return ((gb * 1024).ToString("F0", Culture), "MB");
    }

    private static (string Number, string Unit) Rate(float bytesPerSecond)
    {
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        double v = bytesPerSecond;
        if (v >= gb)
            return ((v / gb).ToString("F2", Culture), "GB/s");
        if (v >= mb)
            return ((v / mb).ToString("F1", Culture), "MB/s");
        if (v >= kb)
            return ((v / kb).ToString(v >= 100 * kb ? "F0" : "F1", Culture), "KB/s");
        return (v.ToString("F0", Culture), "B/s");
    }

    private static string RateAxis(double bytesPerSecond)
    {
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        double v = bytesPerSecond;
        if (v >= gb)
            return (v / gb).ToString("0.#", Culture) + " GB/s";
        if (v >= mb)
            return (v / mb).ToString("0.#", Culture) + " MB/s";
        if (v >= kb)
            return (v / kb).ToString("0.#", Culture) + " KB/s";
        return v.ToString("F0", Culture) + " B/s";
    }
}
