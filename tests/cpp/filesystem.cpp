#include <filesystem>
#include <fstream>
#include <cstdio>

int main(int count, char **arguments)
{
    if (count != 2)
        return 1;
    namespace fs = std::filesystem;
    fs::path directory(arguments[1]);
    std::error_code error;
    if (!fs::create_directory(directory, error) || error || !fs::is_directory(fs::status(directory, error)))
        return 2;
    fs::path original = directory / "original.txt";
    {
        std::ofstream output(original);
        output << "value=42\n";
        if (!output)
            return 3;
    }
    if (!fs::is_regular_file(original, error) || error || fs::file_size(original, error) != 9)
        return 4;
    fs::path renamed = directory / "renamed.txt";
    fs::rename(original, renamed, error);
    if (error || fs::exists(original) || !fs::exists(renamed))
        return 5;
    int entries = 0;
    for (const auto &entry : fs::directory_iterator(directory)) {
        if (entry.path().filename() != "renamed.txt")
            return 6;
        ++entries;
    }
    if (entries != 1 || fs::remove_all(directory, error) != 2 || error)
        return 7;
    std::puts("filesystem status, iteration and operations passed");
    return 0;
}