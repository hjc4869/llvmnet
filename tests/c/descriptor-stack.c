#include </usr/lib/llvm-22/include/flang/ISO_Fortran_binding.h>
#include <stdint.h>
#include <stdio.h>

extern void *_FortranACreateDescriptorStack(const char *, int);
extern void _FortranAPushDescriptor(void *, const CFI_cdesc_t *);
extern void _FortranAPopDescriptor(void *, CFI_cdesc_t *);
extern void _FortranADescriptorAt(void *, uint64_t, CFI_cdesc_t *);
extern void _FortranADestroyDescriptorStack(void *);

int main(void)
{
    int values[16] = {19};
    CFI_CDESC_T(2) descriptor = {
        .base_addr = values, .elem_len = sizeof(int), .version = CFI_VERSION,
        .rank = 2, .type = CFI_type_int32_t, .attribute = CFI_attribute_pointer,
        .dim = {{0, 2, 8}, {-3, 3, 16}}
    };
    CFI_CDESC_T(2) output = {0};
    void *stack = _FortranACreateDescriptorStack(__FILE__, __LINE__);
    for (int index = 0; index < 513; ++index) {
        descriptor.dim[0].lower_bound = index;
        _FortranAPushDescriptor(stack, (CFI_cdesc_t *)&descriptor);
    }
    descriptor.base_addr = NULL;
    descriptor.dim[1].sm = 0;
    values[0] = 23;
    _FortranADescriptorAt(stack, 256, (CFI_cdesc_t *)&output);
    if (output.dim[0].lower_bound != 256 || output.base_addr != values)
        return 1;
    for (int index = 512; index >= 0; --index) {
        _FortranAPopDescriptor(stack, (CFI_cdesc_t *)&output);
        if (output.base_addr != values || output.elem_len != sizeof(int) || output.version != CFI_VERSION || output.rank != 2)
            return 2;
        if (output.type != CFI_type_int32_t || output.attribute != CFI_attribute_pointer || output.extra != 0)
            return 3;
        if (output.dim[0].lower_bound != index || output.dim[0].extent != 2 || output.dim[0].sm != 8)
            return 4;
        if (output.dim[1].lower_bound != -3 || output.dim[1].extent != 3 || output.dim[1].sm != 16 || *(int *)output.base_addr != 23)
            return 5;
    }
    _FortranAPushDescriptor(stack, (CFI_cdesc_t *)&output);
    _FortranADestroyDescriptorStack(stack);
    if (values[0] != 23)
        return 6;
    puts("Fortran descriptor stack: snapshots, indexing, growth and target ownership passed");
    return 0;
}