struct large { long values[8]; };
extern int foreign_aggregate(struct large value);
int main(void)
{
    struct large value = {{0}};
    return foreign_aggregate(value);
}