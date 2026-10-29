cmake_minimum_required(VERSION 3.22)

project(REComposeEngine VERSION 0.1.0 LANGUAGES C CXX)

# The audio engine runs as its own process (recompose-engine) so a UI crash or freeze
# can't stop the music (REL-34, ADR 0001). The app talks to it over a local socket
# (docs/engine-protocol.md). It only executes timed segments and reports state; the plan
# is built in C# (spec 18 §1).

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

if(APPLE AND NOT CMAKE_OSX_DEPLOYMENT_TARGET)
    set(CMAKE_OSX_DEPLOYMENT_TARGET "13.0" CACHE STRING "Minimum macOS version (PLAT-2)" FORCE)
endif()

option(RECOMPOSE_ENGINE_TESTS "Build the engine tests" ON)

# JUCE (AGPLv3, matching this project's licence). To use a local checkout instead of
# downloading, configure with -DFETCHCONTENT_SOURCE_DIR_JUCE=/path/to/JUCE
include(FetchContent)
FetchContent_Declare(JUCE
    GIT_REPOSITORY https://github.com/juce-framework/JUCE.git
    GIT_TAG 9.0.4
    GIT_SHALLOW TRUE
    GIT_PROGRESS TRUE)
FetchContent_MakeAvailable(JUCE)

# r8brain-free (MIT): high-quality resampling when a stem's rate differs from the device's (PB-2).
FetchContent_Declare(r8brain
    GIT_REPOSITORY https://github.com/avaneev/r8brain-free-src.git
    GIT_TAG version-6.5
    GIT_SHALLOW TRUE)
FetchContent_MakeAvailable(r8brain) # no CMakeLists of its own: this only downloads it
add_library(r8brain STATIC ${r8brain_SOURCE_DIR}/r8bbase.cpp)
target_include_directories(r8brain SYSTEM PUBLIC ${r8brain_SOURCE_DIR})
if(WIN32)
    # r8brain's header includes windows.h; keep its min/max macros away from std::min/max.
    target_compile_definitions(r8brain PUBLIC NOMINMAX)
endif()
set_target_properties(r8brain PROPERTIES POSITION_INDEPENDENT_CODE ON)

# Signalsmith Stretch (MIT) with Signalsmith Linear (MIT): live key and tempo on the disk thread when
# no rendered stems exist yet (PB-5 fallback, PB-58, PB-60). Header-only; its CMake fetches Linear 0.6.4.
FetchContent_Declare(signalsmith-stretch
    GIT_REPOSITORY https://github.com/Signalsmith-Audio/signalsmith-stretch.git
    GIT_TAG 1.4.0
    GIT_SHALLOW TRUE)
FetchContent_MakeAvailable(signalsmith-stretch)

# Steinberg ASIO SDK (GPLv3 option, compatible with this project's AGPLv3) for ASIO on
# Windows (PLAT-10). Downloaded at configure time; turn off with -DRECOMPOSE_ASIO=OFF.
option(RECOMPOSE_ASIO "Build with ASIO support on Windows" ON)
set(RECOMPOSE_ASIO_ENABLED 0)
if(WIN32 AND RECOMPOSE_ASIO)
    FetchContent_Declare(asiosdk
        URL https://download.steinberg.net/sdk_downloads/ASIO-SDK_2.3.4_2025-10-15.zip
        URL_HASH SHA256=d5ebf0c20dd2c5f43771fd0c1418f4b361bf52434ee670097cfa6b3a335e2eca
        DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
    FetchContent_MakeAvailable(asiosdk)
    set(RECOMPOSE_ASIO_ENABLED 1)
endif()

# whisper.cpp (MIT) for offline guide transcription (SEC-12, PLAT-40): the same engine
# on both platforms. Built for the CPU only, without the build machine's CPU features
# (GGML_NATIVE off; AVX2 and FMA on x64, so CPUs from 2013 on), and without OpenMP so no
# extra runtime library has to ship. Turn off with -DRECOMPOSE_WHISPER=OFF.
option(RECOMPOSE_WHISPER "Build guide transcription with whisper.cpp" ON)
if(RECOMPOSE_WHISPER)
    set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
    set(WHISPER_BUILD_TESTS OFF CACHE BOOL "" FORCE)
    set(WHISPER_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
    set(WHISPER_BUILD_SERVER OFF CACHE BOOL "" FORCE)
    set(WHISPER_SDL2 OFF CACHE BOOL "" FORCE)
    set(WHISPER_CURL OFF CACHE BOOL "" FORCE)
    set(GGML_NATIVE OFF CACHE BOOL "" FORCE)
    set(GGML_OPENMP OFF CACHE BOOL "" FORCE)
    set(GGML_METAL OFF CACHE BOOL "" FORCE)
    set(GGML_BLAS OFF CACHE BOOL "" FORCE)
    set(GGML_CCACHE OFF CACHE BOOL "" FORCE)
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|AMD64|amd64|x64)$")
        set(GGML_AVX ON CACHE BOOL "" FORCE)
        set(GGML_AVX2 ON CACHE BOOL "" FORCE)
        set(GGML_FMA ON CACHE BOOL "" FORCE)
        set(GGML_F16C ON CACHE BOOL "" FORCE)
    endif()
    FetchContent_Declare(whisper
        GIT_REPOSITORY https://github.com/ggml-org/whisper.cpp.git
        GIT_TAG v1.9.5
        GIT_SHALLOW TRUE)
    FetchContent_MakeAvailable(whisper)
    # Their headers are not ours to warn about.
    foreach(target whisper ggml)
        get_target_property(dirs ${target} INTERFACE_INCLUDE_DIRECTORIES)
        set_target_properties(${target} PROPERTIES INTERFACE_SYSTEM_INCLUDE_DIRECTORIES "${dirs}")
    endforeach()
endif()

# JUCE modules compiled once into a static library that the engine and the tests share
# (JUCE's own "shared code" pattern: module sources are added only to this target).
add_library(recompose_juce STATIC)

target_link_libraries(recompose_juce
    PRIVATE
        juce::juce_core
        juce::juce_events
        juce::juce_audio_basics
        juce::juce_audio_formats
        juce::juce_audio_devices
    PUBLIC
        juce::juce_recommended_config_flags
        juce::juce_recommended_warning_flags)

target_compile_definitions(recompose_juce
    PUBLIC
        JUCE_GLOBAL_MODULE_SETTINGS_INCLUDED=1
        JUCE_STANDALONE_APPLICATION=0
        JUCE_USE_CURL=0
        JUCE_WEB_BROWSER=0
        JUCE_MODAL_LOOPS_PERMITTED=0
        JUCE_JACK=0
        # Stems arrive as WAV, AIFF, FLAC, Ogg or MP3 (JUCE's own MP3 decoder). Opus and
        # Windows Media are off so both platforms read the same formats (PLAT-1).
        JUCE_USE_OPUS=0
        JUCE_USE_WINDOWS_MEDIA_FORMAT=0
        JUCE_USE_MP3AUDIOFORMAT=1
        JUCE_ASIO=${RECOMPOSE_ASIO_ENABLED})

if(RECOMPOSE_ASIO_ENABLED)
    target_include_directories(recompose_juce PRIVATE ${asiosdk_SOURCE_DIR}/common)
endif()


# Consumers see the module headers and the module-availability definitions too.
target_include_directories(recompose_juce
    INTERFACE $<TARGET_PROPERTY:recompose_juce,INCLUDE_DIRECTORIES>)
target_compile_definitions(recompose_juce
    INTERFACE $<TARGET_PROPERTY:recompose_juce,COMPILE_DEFINITIONS>)

# Everything except main(): protocol, sockets, control loop, audio graph, tools.
add_library(recompose_engine_core STATIC
    src/ipc/frame.cpp
    src/ipc/local_socket.cpp
    src/engine_server.cpp
    src/message_handler.cpp
    src/engine.cpp
    src/tools.cpp
    src/analysis.cpp
    src/audio/song.cpp
    src/audio/click.cpp
    src/audio/stretcher.cpp
    src/audio/pads.cpp
    src/audio/transition.cpp
    src/audio/disk_streamer.cpp
    src/audio/player.cpp
    src/audio/silent_clock.cpp
    src/audio/voice_input.cpp)

target_include_directories(recompose_engine_core PUBLIC src)
target_compile_definitions(recompose_engine_core PUBLIC RECOMPOSE_ENGINE_VERSION="${PROJECT_VERSION}")
target_link_libraries(recompose_engine_core PUBLIC recompose_juce r8brain signalsmith-stretch)
if(WIN32)
    target_link_libraries(recompose_engine_core PUBLIC ws2_32)
endif()
if(RECOMPOSE_WHISPER)
    target_link_libraries(recompose_engine_core PUBLIC whisper)
    target_compile_definitions(recompose_engine_core PUBLIC RECOMPOSE_WITH_WHISPER=1)
else()
    target_compile_definitions(recompose_engine_core PUBLIC RECOMPOSE_WITH_WHISPER=0)
endif()

add_executable(recompose_engine src/main.cpp)
target_link_libraries(recompose_engine PRIVATE recompose_engine_core)
if(APPLE)
    target_link_libraries(recompose_engine PRIVATE "-framework CoreFoundation")
endif()
set_target_properties(recompose_engine PROPERTIES
    OUTPUT_NAME recompose-engine
    RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)

# `cmake --install <build> --component engine --prefix artifacts/native` places the
# executable where the .NET projects pick it up (artifacts/native/bin, see
# Directory.Build.props). The component keeps JUCE's own install rules out.
install(TARGETS recompose_engine
    RUNTIME DESTINATION bin COMPONENT engine)

if(RECOMPOSE_ENGINE_TESTS)
    enable_testing()
    add_subdirectory(tests)
endif()
