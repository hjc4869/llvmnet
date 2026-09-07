#include <libgen.h>
#include <string.h>

static int check(const char *input, const char *base, const char *directory)
{
    char base_path[128];
    char directory_path[128];
    strcpy(base_path, input);
    strcpy(directory_path, input);
    return strcmp(basename(base_path), base) || strcmp(dirname(directory_path), directory);
}

int main(void)
{
    if (strcmp(basename(0), ".") || strcmp(dirname(0), ".")) return 1;
    if (check("", ".", ".")) return 2;
    if (check("/", "/", "/")) return 3;
    if (check("///", "/", "/")) return 4;
    if (check("file", "file", ".")) return 5;
    if (check("/file", "file", "/")) return 6;
    if (check("/dir/file", "file", "/dir")) return 7;
    if (check("dir/file///", "file", "dir")) return 8;
    if (check("dir///file", "file", "dir")) return 9;
    if (check(".", ".", ".")) return 10;
    if (check("..", "..", ".")) return 11;
    return 0;
}