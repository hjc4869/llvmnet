long invoke_variadic(long (*callback)(int, ...))
{
    return callback(1, 3, 4.0);
}