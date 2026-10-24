// SPDX-License-Identifier: AGPL-3.0-or-later
//
// A deliberately tiny test harness: TEST registers a function, CHECK records a failure
// and keeps going. Swap for Catch2 if the engine tests outgrow it.
#pragma once

#include <cstdio>
#include <functional>
#include <string>
#include <vector>

namespace rec::test
{
struct Case
{
    const char* name;
    std::function<void()> body;
};

std::vector<Case>& registry();
int& failures();

struct Registrar
{
    Registrar (const char* name, std::function<void()> body) { registry().push_back ({ name, std::move (body) }); }
};
} // namespace rec::test

#define REC_CONCAT_INNER(a, b) a##b
#define REC_CONCAT(a, b) REC_CONCAT_INNER (a, b)

#define TEST(name)                                                                       \
    static void REC_CONCAT (test_, __LINE__)();                                          \
    static const rec::test::Registrar REC_CONCAT (registrar_, __LINE__) (name, REC_CONCAT (test_, __LINE__)); \
    static void REC_CONCAT (test_, __LINE__)()

#define CHECK(condition)                                                                 \
    do                                                                                   \
    {                                                                                    \
        if (! (condition))                                                               \
        {                                                                                \
            std::fprintf (stderr, "%s:%d: CHECK failed: %s\n", __FILE__, __LINE__, #condition); \
            ++rec::test::failures();                                                     \
        }                                                                                \
    } while (0)
