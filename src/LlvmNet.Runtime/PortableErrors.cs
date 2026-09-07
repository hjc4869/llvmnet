namespace LlvmNet.Runtime;

internal static class PortableErrors
{
    internal static string Message(int error) => error switch
    {
        0 => "Success",
        1 => "Operation not permitted",
        2 => "No such file or directory",
        3 => "No such process",
        4 => "Interrupted system call",
        5 => "Input/output error",
        6 => "No such device or address",
        7 => "Argument list too long",
        9 => "Bad file descriptor",
        10 => "No child processes",
        11 => "Resource temporarily unavailable",
        12 => "Cannot allocate memory",
        13 => "Permission denied",
        14 => "Bad address",
        16 => "Device or resource busy",
        17 => "File exists",
        18 => "Invalid cross-device link",
        19 => "No such device",
        20 => "Not a directory",
        21 => "Is a directory",
        22 => "Invalid argument",
        23 => "Too many open files in system",
        24 => "Too many open files",
        25 => "Inappropriate ioctl for device",
        27 => "File too large",
        28 => "No space left on device",
        29 => "Illegal seek",
        30 => "Read-only file system",
        32 => "Broken pipe",
        33 => "Numerical argument out of domain",
        34 => "Numerical result out of range",
        35 => "Resource deadlock avoided",
        36 => "File name too long",
        38 => "Function not implemented",
        39 => "Directory not empty",
        75 => "Value too large for defined data type",
        95 => "Operation not supported",
        110 => "Connection timed out",
        _ => "Unknown error " + error.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };
}