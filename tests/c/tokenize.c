#include <string.h>

int main(void)
{
    char input[] = ",,first:second;third;;";
    char *token = strtok(input, ",:");
    if (!token || strcmp(token, "first")) return 1;
    token = strtok(0, ";");
    if (!token || strcmp(token, "second")) return 2;
    token = strtok(0, ";");
    if (!token || strcmp(token, "third")) return 3;
    if (strtok(0, ";") || strtok(0, ";")) return 4;
    char empty[] = "";
    if (strtok(empty, "")) return 5;
    char whole[] = "no separators";
    token = strtok(whole, "");
    if (!token || strcmp(token, "no separators") || strtok(0, "")) return 6;
    char first[] = "a,b";
    char second[] = "1:2";
    char *first_state = 0, *second_state = 0;
    token = strtok_r(first, ",", &first_state);
    if (!token || strcmp(token, "a")) return 7;
    token = strtok_r(second, ":", &second_state);
    if (!token || strcmp(token, "1")) return 8;
    token = strtok_r(0, ",", &first_state);
    if (!token || strcmp(token, "b")) return 9;
    token = strtok_r(0, ":", &second_state);
    if (!token || strcmp(token, "2")) return 10;
    if (strtok_r(0, ",", &first_state) || strtok_r(0, ":", &second_state)) return 11;
    return 0;
}