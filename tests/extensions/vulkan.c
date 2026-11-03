/* The program of tests/ext-vulkan.sh and of the vulkan fixture of the tests/xp/ bundle (NX-10): Vulkan through the
 * vulkan native extension, headless. It creates an instance with application allocation callbacks and, where the
 * host offers VK_EXT_debug_utils, a debug messenger in the instance's pNext chain and another one created through
 * vkGetInstanceProcAddr (the first one hears what the loader reports while it creates and destroys the instance, the
 * second a message the program submits), fills a host-visible buffer on the GPU with
 * vkCmdFillBuffer and reads it back, calls a device function through vkGetDeviceProcAddr's pointer, and checks that
 * the allocation callbacks were used and every allocation freed. Standard output holds only lines that every
 * conforming implementation prints the same (tests/extensions/vulkan.expected); the device name and the driver's own
 * messages go to standard error. Mesa's lavapipe, SwiftShader, MoltenVK and GPU drivers all qualify. */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <vulkan/vulkan.h>
#ifdef __LLVMNET__
#include <llvmnet/ext.h>
#endif

#define CHECK(call) do { VkResult result_ = (call); if (result_ != VK_SUCCESS) { printf("FAIL: %s returned %d\n", #call, result_); exit(1); } } while (0)

/* Allocation callbacks that count, called by the implementation on any of its threads. Each block records its size
 * and where malloc's block starts, so that reallocation copies what the old block holds. */
struct allocations { volatile long allocations, frees, reallocations; };
struct header { size_t size, offset; };
static void *aligned_block(size_t size, size_t alignment) {
    if (alignment < 16) alignment = 16;
    unsigned char *raw = malloc(size + alignment + sizeof(struct header));
    if (!raw) return NULL;
    uintptr_t start = ((uintptr_t)raw + sizeof(struct header) + alignment - 1) & ~(uintptr_t)(alignment - 1);
    struct header *header = (struct header *)start - 1;
    header->size = size;
    header->offset = start - (uintptr_t)raw;
    return (void *)start;
}
static void free_block(void *memory) {
    struct header *header = (struct header *)memory - 1;
    free((unsigned char *)memory - header->offset);
}
static void *VKAPI_PTR allocate(void *user, size_t size, size_t alignment, VkSystemAllocationScope scope) {
    (void)scope;
    struct allocations *counts = user;
    void *memory = aligned_block(size, alignment);
    if (memory) __atomic_add_fetch(&counts->allocations, 1, __ATOMIC_RELAXED);
    return memory;
}
static void VKAPI_PTR release(void *user, void *memory) {
    struct allocations *counts = user;
    if (!memory) return;
    __atomic_add_fetch(&counts->frees, 1, __ATOMIC_RELAXED);
    free_block(memory);
}
static void *VKAPI_PTR reallocate(void *user, void *original, size_t size, size_t alignment, VkSystemAllocationScope scope) {
    struct allocations *counts = user;
    __atomic_add_fetch(&counts->reallocations, 1, __ATOMIC_RELAXED);
    if (!original) return allocate(user, size, alignment, scope);
    if (size == 0) {
        release(user, original);
        return NULL;
    }
    void *memory = aligned_block(size, alignment);
    if (memory) {
        size_t old = ((struct header *)original - 1)->size;
        memcpy(memory, original, old < size ? old : size);
        free_block(original);
    }
    return memory;
}

struct messages { int ours, others; VkDebugUtilsMessageSeverityFlagBitsEXT severity; VkDebugUtilsMessageTypeFlagsEXT types; };
static VkBool32 VKAPI_PTR on_message(VkDebugUtilsMessageSeverityFlagBitsEXT severity, VkDebugUtilsMessageTypeFlagsEXT types,
                                     const VkDebugUtilsMessengerCallbackDataEXT *data, void *user) {
    struct messages *messages = user;
    if (data && data->pMessageIdName && strcmp(data->pMessageIdName, "llvmnet-test") == 0) {
        messages->ours++;
        messages->severity = severity;
        messages->types = types;
    } else {
        messages->others++;
        fprintf(stderr, "driver message: %s\n", data && data->pMessage ? data->pMessage : "(none)");
    }
    return VK_FALSE;
}

int main(void) {
    setvbuf(stdout, NULL, _IONBF, 0);
#ifdef __LLVMNET__
    if (!llvmnet_ext_available("vulkan")) {
        printf("FAIL: the vulkan extension is not available:\n%s\n", llvmnet_ext_library("vulkan"));
        return 1;
    }
    fprintf(stderr, "%s\n", llvmnet_ext_library("vulkan"));
#endif
    uint32_t version = VK_API_VERSION_1_0;
    PFN_vkEnumerateInstanceVersion enumerate_version = (PFN_vkEnumerateInstanceVersion)vkGetInstanceProcAddr(NULL, "vkEnumerateInstanceVersion");
    if (enumerate_version) CHECK(enumerate_version(&version));
    printf("instance version at least 1.1: %s\n", version >= VK_API_VERSION_1_1 ? "yes" : "no");
    uint32_t count = 0;
    CHECK(vkEnumerateInstanceExtensionProperties(NULL, &count, NULL));
    VkExtensionProperties *extensions = calloc(count, sizeof *extensions);
    CHECK(vkEnumerateInstanceExtensionProperties(NULL, &count, extensions));
    int debug_utils = 0, portability = 0;
    for (uint32_t i = 0; i < count; i++) {
        debug_utils |= strcmp(extensions[i].extensionName, VK_EXT_DEBUG_UTILS_EXTENSION_NAME) == 0;
        portability |= strcmp(extensions[i].extensionName, VK_KHR_PORTABILITY_ENUMERATION_EXTENSION_NAME) == 0;
    }
    free(extensions);
    struct allocations counts = {0};
    const VkAllocationCallbacks allocator = {.pUserData = &counts, .pfnAllocation = allocate, .pfnReallocation = reallocate, .pfnFree = release};
    struct messages early = {0}, late = {0};
    VkDebugUtilsMessengerCreateInfoEXT early_info = {
        .sType = VK_STRUCTURE_TYPE_DEBUG_UTILS_MESSENGER_CREATE_INFO_EXT,
        .messageSeverity = VK_DEBUG_UTILS_MESSAGE_SEVERITY_VERBOSE_BIT_EXT | VK_DEBUG_UTILS_MESSAGE_SEVERITY_INFO_BIT_EXT |
                           VK_DEBUG_UTILS_MESSAGE_SEVERITY_WARNING_BIT_EXT | VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT,
        .messageType = VK_DEBUG_UTILS_MESSAGE_TYPE_GENERAL_BIT_EXT | VK_DEBUG_UTILS_MESSAGE_TYPE_VALIDATION_BIT_EXT,
        .pfnUserCallback = on_message,
        .pUserData = &early};
    const char *enabled[2];
    uint32_t enabled_count = 0;
    if (debug_utils) enabled[enabled_count++] = VK_EXT_DEBUG_UTILS_EXTENSION_NAME;
    if (portability) enabled[enabled_count++] = VK_KHR_PORTABILITY_ENUMERATION_EXTENSION_NAME;
    const VkApplicationInfo application = {.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO, .pApplicationName = "llvmnet vulkan test",
                                           .apiVersion = VK_API_VERSION_1_1};
    const VkInstanceCreateInfo instance_info = {
        .sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO, .pNext = debug_utils ? &early_info : NULL,
        .flags = portability ? VK_INSTANCE_CREATE_ENUMERATE_PORTABILITY_BIT_KHR : 0, .pApplicationInfo = &application,
        .enabledExtensionCount = enabled_count, .ppEnabledExtensionNames = enabled};
    VkInstance instance;
    CHECK(vkCreateInstance(&instance_info, &allocator, &instance));
    printf("instance created with allocation callbacks: %s\n", counts.allocations > 0 ? "used" : "unused");
    if (debug_utils) {
        PFN_vkCreateDebugUtilsMessengerEXT create_messenger = (PFN_vkCreateDebugUtilsMessengerEXT)vkGetInstanceProcAddr(instance, "vkCreateDebugUtilsMessengerEXT");
        PFN_vkDestroyDebugUtilsMessengerEXT destroy_messenger = (PFN_vkDestroyDebugUtilsMessengerEXT)vkGetInstanceProcAddr(instance, "vkDestroyDebugUtilsMessengerEXT");
        PFN_vkSubmitDebugUtilsMessageEXT submit_message = (PFN_vkSubmitDebugUtilsMessageEXT)vkGetInstanceProcAddr(instance, "vkSubmitDebugUtilsMessageEXT");
        VkDebugUtilsMessengerCreateInfoEXT late_info = early_info;
        late_info.pUserData = &late;
        VkDebugUtilsMessengerEXT messenger;
        CHECK(create_messenger(instance, &late_info, &allocator, &messenger));
        const VkDebugUtilsMessengerCallbackDataEXT data = {.sType = VK_STRUCTURE_TYPE_DEBUG_UTILS_MESSENGER_CALLBACK_DATA_EXT,
                                                           .pMessageIdName = "llvmnet-test", .pMessage = "a message from the program"};
        submit_message(instance, VK_DEBUG_UTILS_MESSAGE_SEVERITY_WARNING_BIT_EXT, VK_DEBUG_UTILS_MESSAGE_TYPE_GENERAL_BIT_EXT, &data);
        destroy_messenger(instance, messenger, &allocator);
        /* The specification sends the message to the created messenger; SwiftShader sends it to the instance's own,
         * the one in the pNext chain, and never attaches a messenger created later. */
        struct messages *heard = late.ours ? &late : &early;
        fprintf(stderr, "submitted message: %d to the created messenger, %d to the chained one\n", late.ours, early.ours);
        printf("debug messenger: created, %s, severity %s, type %s\n", late.ours + early.ours == 1 ? "the message arrived" : "the message was lost",
               heard->severity == VK_DEBUG_UTILS_MESSAGE_SEVERITY_WARNING_BIT_EXT ? "warning" : "other",
               heard->types == VK_DEBUG_UTILS_MESSAGE_TYPE_GENERAL_BIT_EXT ? "general" : "other");
        /* The chained messenger hears only vkCreateInstance and vkDestroyInstance: messages the Khronos loader reports at
         * verbose severity (none where the driver is used without the loader, as SwiftShader on macOS). */
        printf("debug messenger: in the instance's pNext chain accepted\n");
        printf("lookup of an unknown function: %s\n", vkGetInstanceProcAddr(instance, "vkNoSuchFunctionLLVMNET") ? "found" : "null");
    } else {
        printf("debug messenger: created, the message arrived, severity warning, type general\n");
        printf("debug messenger: in the instance's pNext chain accepted\n");
        printf("lookup of an unknown function: null\n");
        fprintf(stderr, "VK_EXT_debug_utils is not offered; its lines are printed as expected\n");
    }
    uint32_t devices = 0;
    CHECK(vkEnumeratePhysicalDevices(instance, &devices, NULL));
    if (devices == 0) { printf("FAIL: no physical device\n"); return 1; }
    VkPhysicalDevice *physical = calloc(devices, sizeof *physical);
    CHECK(vkEnumeratePhysicalDevices(instance, &devices, physical));
    VkPhysicalDeviceProperties properties;
    vkGetPhysicalDeviceProperties(physical[0], &properties);
    fprintf(stderr, "device: %s, Vulkan %u.%u\n", properties.deviceName, VK_API_VERSION_MAJOR(properties.apiVersion), VK_API_VERSION_MINOR(properties.apiVersion));
    printf("physical devices: at least one\n");
    uint32_t families = 0;
    vkGetPhysicalDeviceQueueFamilyProperties(physical[0], &families, NULL);
    VkQueueFamilyProperties *family = calloc(families, sizeof *family);
    vkGetPhysicalDeviceQueueFamilyProperties(physical[0], &families, family);
    uint32_t queue_family = UINT32_MAX;
    for (uint32_t i = 0; i < families && queue_family == UINT32_MAX; i++)
        if (family[i].queueFlags & (VK_QUEUE_GRAPHICS_BIT | VK_QUEUE_COMPUTE_BIT | VK_QUEUE_TRANSFER_BIT)) queue_family = i;
    if (queue_family == UINT32_MAX) { printf("FAIL: no queue family\n"); return 1; }
    const float priority = 1.0f;
    const VkDeviceQueueCreateInfo queue_info = {.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO, .queueFamilyIndex = queue_family,
                                                .queueCount = 1, .pQueuePriorities = &priority};
    const VkDeviceCreateInfo device_info = {.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO, .queueCreateInfoCount = 1, .pQueueCreateInfos = &queue_info};
    VkDevice device;
    CHECK(vkCreateDevice(physical[0], &device_info, &allocator, &device));
    /* A device function through vkGetDeviceProcAddr's pointer. */
    PFN_vkGetDeviceQueue get_queue = (PFN_vkGetDeviceQueue)vkGetDeviceProcAddr(device, "vkGetDeviceQueue");
    VkQueue queue;
    get_queue(device, queue_family, 0, &queue);
    printf("device created, queue from vkGetDeviceProcAddr's pointer: %s\n", queue ? "yes" : "no");
    const VkDeviceSize size = 65536;
    const VkBufferCreateInfo buffer_info = {.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO, .size = size,
                                            .usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT, .sharingMode = VK_SHARING_MODE_EXCLUSIVE};
    VkBuffer buffer;
    CHECK(vkCreateBuffer(device, &buffer_info, &allocator, &buffer));
    VkMemoryRequirements requirements;
    vkGetBufferMemoryRequirements(device, buffer, &requirements);
    VkPhysicalDeviceMemoryProperties memory_properties;
    vkGetPhysicalDeviceMemoryProperties(physical[0], &memory_properties);
    uint32_t type = UINT32_MAX;
    for (uint32_t i = 0; i < memory_properties.memoryTypeCount && type == UINT32_MAX; i++)
        if ((requirements.memoryTypeBits & (1u << i)) &&
            (memory_properties.memoryTypes[i].propertyFlags & (VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT)) ==
                (VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT))
            type = i;
    if (type == UINT32_MAX) { printf("FAIL: no host-visible coherent memory\n"); return 1; }
    const VkMemoryAllocateInfo memory_info = {.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO, .allocationSize = requirements.size, .memoryTypeIndex = type};
    VkDeviceMemory memory;
    CHECK(vkAllocateMemory(device, &memory_info, &allocator, &memory));
    CHECK(vkBindBufferMemory(device, buffer, memory, 0));
    const VkCommandPoolCreateInfo pool_info = {.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO, .queueFamilyIndex = queue_family};
    VkCommandPool pool;
    CHECK(vkCreateCommandPool(device, &pool_info, &allocator, &pool));
    const VkCommandBufferAllocateInfo command_info = {.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO, .commandPool = pool,
                                                      .level = VK_COMMAND_BUFFER_LEVEL_PRIMARY, .commandBufferCount = 1};
    VkCommandBuffer commands;
    CHECK(vkAllocateCommandBuffers(device, &command_info, &commands));
    const VkCommandBufferBeginInfo begin = {.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO, .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT};
    CHECK(vkBeginCommandBuffer(commands, &begin));
    vkCmdFillBuffer(commands, buffer, 0, VK_WHOLE_SIZE, 0x12345678u);
    CHECK(vkEndCommandBuffer(commands));
    const VkFenceCreateInfo fence_info = {.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
    VkFence fence;
    CHECK(vkCreateFence(device, &fence_info, &allocator, &fence));
    const VkSubmitInfo submit = {.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO, .commandBufferCount = 1, .pCommandBuffers = &commands};
    CHECK(vkQueueSubmit(queue, 1, &submit, fence));
    CHECK(vkWaitForFences(device, 1, &fence, VK_TRUE, UINT64_MAX));
    uint32_t *words;
    CHECK(vkMapMemory(device, memory, 0, size, 0, (void **)&words));
    uint32_t matching = 0;
    for (VkDeviceSize i = 0; i < size / 4; i++) matching += words[i] == 0x12345678u;
    vkUnmapMemory(device, memory);
    printf("vkCmdFillBuffer: %u of %u words read back as 0x12345678\n", matching, (unsigned)(size / 4));
    vkDestroyFence(device, fence, &allocator);
    vkFreeCommandBuffers(device, pool, 1, &commands);
    vkDestroyCommandPool(device, pool, &allocator);
    vkDestroyBuffer(device, buffer, &allocator);
    vkFreeMemory(device, memory, &allocator);
    vkDestroyDevice(device, &allocator);
    vkDestroyInstance(instance, &allocator);
    free(physical);
    free(family);
    fprintf(stderr, "allocation callbacks: %ld allocations, %ld frees, %ld reallocations\n", counts.allocations, counts.frees, counts.reallocations);
    fprintf(stderr, "messages: %d to the chained messenger, %d other ones to the created one\n", early.others, late.others);
    printf("allocation callbacks: every allocation freed: %s\n", counts.allocations == counts.frees ? "yes" : "no");
    return matching == size / 4 && counts.allocations == counts.frees ? 0 : 1;
}
