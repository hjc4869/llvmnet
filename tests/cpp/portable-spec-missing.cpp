#if PROBE == 1
#include <iostream>
int main() { std::cout << 1 << std::endl; }
#elif PROBE == 2
#include <sstream>
int main() { std::istringstream input("1"); int value; input >> value; return value != 1; }
#elif PROBE == 3
#include <fstream>
int main() { std::ifstream input("probe.txt"); return !input; }
#elif PROBE == 4
#include <complex>
int main() { return 0; }
#elif PROBE == 5
#include <thread>
int main() { std::thread worker([] {}); worker.join(); }
#endif