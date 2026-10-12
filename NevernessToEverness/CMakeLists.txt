cmake_minimum_required(VERSION 3.24)
project(NevernessToEvernessLaunchBridge VERSION 1.0.0 LANGUAGES CXX RC)

function(configure_bridge_target target)
    target_compile_features(${target} PRIVATE cxx_std_20)
    target_compile_definitions(${target} PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
    target_compile_options(${target} PRIVATE /W4 /permissive- /EHsc /utf-8)
    target_link_options(${target} PRIVATE /MANIFEST:NO)
    target_link_libraries(${target} PRIVATE oleacc oleaut32 dbghelp shell32)
    set_property(TARGET ${target} PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endfunction()

add_executable(NevernessToEvernessLaunchBridge WIN32 src/main.cpp src/app.rc)
configure_bridge_target(NevernessToEvernessLaunchBridge)

add_executable(NevernessToEvernessLaunchBridgeTestHost WIN32 src/main.cpp src/app.rc)
target_compile_definitions(NevernessToEvernessLaunchBridgeTestHost PRIVATE NTE_TEST_BUILD)
configure_bridge_target(NevernessToEvernessLaunchBridgeTestHost)

install(TARGETS NevernessToEvernessLaunchBridge RUNTIME DESTINATION .)
