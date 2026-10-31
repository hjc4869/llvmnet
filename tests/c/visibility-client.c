extern int exported_function(int value);
extern int exported_user(int value);
#ifdef USE_HIDDEN
extern int hidden_function(int value);
#endif
int main(void)
{
    int failures = exported_function(1) != 2;
    failures += exported_user(1) != 6;
#ifdef USE_HIDDEN
    failures += hidden_function(1) != 3;
#endif
    return failures;
}
