// SPDX-License-Identifier: AGPL-3.0-or-later
#include "check.h"

#include "ipc/local_socket.h"

namespace rec::test
{
std::vector<Case>& registry()
{
    static std::vector<Case> cases;
    return cases;
}

int& failures()
{
    static int count = 0;
    return count;
}
} // namespace rec::test

int main()
{
    rec::ipc::SocketLibrary sockets;
    if (! sockets.ok())
    {
        std::fprintf (stderr, "socket library could not be initialised\n");
        return 1;
    }

    for (const auto& c : rec::test::registry())
    {
        const int before = rec::test::failures();
        c.body();
        std::printf ("%s %s\n", rec::test::failures() == before ? "PASS" : "FAIL", c.name);
    }

    std::printf ("%zu tests, %d failed checks\n", rec::test::registry().size(), rec::test::failures());
    return rec::test::failures() == 0 ? 0 : 1;
}
