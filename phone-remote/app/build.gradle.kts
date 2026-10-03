plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.couchtv.remote"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.couchtv.remote"
        minSdk = 26          // every Xiaomi phone with an IR blaster runs Android 8 or newer
        targetSdk = 35
        versionCode = 1
        versionName = "1.0"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
}

// No libraries: the app only uses the Android framework (ConsumerIrManager for the IR blaster).
