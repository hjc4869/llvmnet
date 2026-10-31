/* Time zones from zoneinfo: localtime and mktime over instants between 1902 and 2037 in the zones named on the
 * command line, then the local zone. The portable build runs with the host's zoneinfo files and with the TZif images
 * the host layer generates from .NET's TimeZoneInfo (LLVMNET_ZONEINFO=managed); both must print what the native
 * build prints. Abbreviations (%Z) are left out: the generated images use numeric ones. */
#include <stdio.h>
#include <stdlib.h>
#include <time.h>

static void line(time_t moment)
{
    struct tm local, copy;
    char text[64];
    localtime_r(&moment, &local);
    strftime(text, sizeof text, "%Y-%m-%d %H:%M:%S %z %a %j", &local);
    copy = local;
    printf("%lld %s %d %lld\n", (long long)moment, text, local.tm_isdst, (long long)mktime(&copy));
}

static void zone(const char *name, int dense)
{
    if (name) setenv("TZ", name, 1);
    else unsetenv("TZ");
    tzset();
    printf("zone %s\n", name ? name : "(local)");
    /* Every 37 days and 5 hours from 1902 through 2037, so the samples drift through the day and the year. */
    for (time_t moment = -2145916800LL; moment < 2145916800LL; moment += 37 * 86400 + 5 * 3600) line(moment);
    /* Every hour of 2024 for the zones whose transitions the test wants seen exactly. */
    if (dense)
        for (time_t moment = 1704067200LL; moment < 1735689600LL; moment += 3600) line(moment);
    /* Local times mktime must normalize: an out-of-range day, and in the zones with daylight time an overlap with
     * the daylight flag given. (A time in a gap, or an explicit flag in a zone without daylight time, is where glibc
     * and musl make different valid choices, so those are not probed.) */
    struct tm local = { .tm_year = 124, .tm_mon = 0, .tm_mday = 40, .tm_hour = 12, .tm_isdst = -1 };
    printf("mktime normalized %lld %d-%d\n", (long long)mktime(&local), local.tm_mon + 1, local.tm_mday);
    if (!dense) return;
    local = (struct tm){ .tm_year = 124, .tm_mon = 9, .tm_mday = 27, .tm_hour = 2, .tm_min = 30, .tm_isdst = 0 };
    printf("mktime overlap standard %lld\n", (long long)mktime(&local));
    local = (struct tm){ .tm_year = 124, .tm_mon = 9, .tm_mday = 27, .tm_hour = 2, .tm_min = 30, .tm_isdst = 1 };
    printf("mktime overlap daylight %lld\n", (long long)mktime(&local));
}

int main(int argc, char **argv)
{
    for (int index = 1; index < argc; index++) zone(argv[index], index <= 3);
    zone(0, 0);
    return 0;
}
