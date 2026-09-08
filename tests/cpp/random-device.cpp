#include <cstdio>
#include <random>
#include <stdexcept>

int main()
{
    for (const char *token : {"default", "/dev/urandom"}) {
        std::random_device device(token);
        if (!(device.entropy() >= 0 && device.entropy() <= 32))
            return 1;
        for (int index = 0; index < 64; ++index) {
            auto value = device();
            if (value < device.min() || value > device.max())
                return 2;
        }
    }
    try {
        std::random_device invalid("llvmnet-invalid-token");
        return 3;
    } catch (const std::runtime_error &) {
    }
    std::mt19937 engine(12345);
    std::uniform_int_distribution<int> distribution(0, 1000);
    unsigned long long total = 0;
    for (int index = 0; index < 100; ++index)
        total += distribution(engine);
    std::printf("random device and seeded engine: %llu\n", total);
}