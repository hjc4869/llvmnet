// Force-included after gtest.h where gtest has no death tests: on a target it does not recognize as an operating
// system with fork (llvmnet's portable mode), EXPECT_DEATH and its relatives would not compile. Each one fails its
// test with a reason the runner classifies instead, as the death tests of libc++'s suite are.
#include "gtest/gtest.h"
#if !GTEST_HAS_DEATH_TEST
#define LLVMNET_DEATH_TEST_REASON "death test (needs fork)"
#define EXPECT_DEATH(statement, matcher) ADD_FAILURE() << LLVMNET_DEATH_TEST_REASON
#define ASSERT_DEATH(statement, matcher) FAIL() << LLVMNET_DEATH_TEST_REASON
#define EXPECT_EXIT(statement, predicate, matcher) ADD_FAILURE() << LLVMNET_DEATH_TEST_REASON
#define ASSERT_EXIT(statement, predicate, matcher) FAIL() << LLVMNET_DEATH_TEST_REASON
namespace testing {
struct ExitedWithCode { explicit ExitedWithCode(int) {} };
struct KilledBySignal { explicit KilledBySignal(int) {} };
} // namespace testing
#endif
