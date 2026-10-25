add_executable(recompose_engine_tests
    test_main.cpp
    test_frame.cpp
    test_message_handler.cpp
    test_engine_server.cpp
    test_player.cpp
    test_pads.cpp
    test_analysis.cpp)

target_link_libraries(recompose_engine_tests PRIVATE recompose_engine_core)

set_target_properties(recompose_engine_tests PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)

add_test(NAME engine_unit COMMAND recompose_engine_tests)

# The real executable starts and reports its version.
add_test(NAME engine_version COMMAND recompose_engine --version)
set_tests_properties(engine_version PROPERTIES PASS_REGULAR_EXPRESSION "recompose-engine 0\\.1\\.0 \\(JUCE v[0-9.]+\\), protocol [0-9]+")
