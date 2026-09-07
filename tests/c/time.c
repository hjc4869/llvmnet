#include <time.h>
#include <stdio.h>
#include <string.h>
int main(void)
{
    time_t timestamp = 1709251199;
    struct tm result;
    if (!gmtime_r(&timestamp, &result)) return 1;
    if (result.tm_year != 124 || result.tm_mon != 1 || result.tm_mday != 29 || result.tm_hour != 23 || result.tm_min != 59 || result.tm_sec != 59) return 2;
    if (result.tm_yday != 59 || result.tm_wday != 4 || result.tm_isdst != 0) return 3;
    char buffer[128];
    if (!strftime(buffer, sizeof(buffer), "%F %T %a %j %V %z", &result)) return 4;
    if (strcmp(buffer, "2024-02-29 23:59:59 Thu 060 09 +0000")) return 5;
    if (timegm(&result) != timestamp) return 6;
    struct timespec first, second;
    if (clock_gettime(CLOCK_MONOTONIC, &first) || clock_gettime(CLOCK_MONOTONIC, &second)) return 7;
    if (second.tv_sec < first.tv_sec || second.tv_nsec < 0 || second.tv_nsec >= 1000000000) return 8;
    puts(buffer);
    return 0;
}