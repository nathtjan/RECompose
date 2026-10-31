add_executable(recompose_engine_tests
    test_main.cpp
    test_frame.cpp
    test_message_handler.cpp
    test_engine_server.cpp
    test_player.cpp
    test_pads.cpp
    test_transition.cpp
    test_live.cpp
    test_analysis.cpp
    test_silent_clock.cpp
    test_voice.cpp
    test_render.cpp)

target_link_libraries(recompose_engine_tests PRIVATE recompose_engine_core)

set_target_properties(recompose_engine_tests PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)

add_test(NAME engine_unit COMMAND recompose_engine_tests)

# The real executable starts and reports its version: the app's, from Directory.Build.props.
string(REPLACE "." "\\." REC_VERSION_PATTERN "${RECOMPOSE_VERSION}")
add_test(NAME engine_version COMMAND recompose_engine --version)
set_tests_properties(engine_version PROPERTIES PASS_REGULAR_EXPRESSION "recompose-engine ${REC_VERSION_PATTERN} \\(JUCE v[0-9.]+\\), protocol [0-9]+")
