plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// GitHub Actions passes -PversionCode=<build number> -Pcommit=<sha>. Local builds are version 1, so any published
// build counts as newer and the app offers it.
val buildNumber = (findProperty("versionCode") as String?)?.toIntOrNull() ?: 1
val commit = (findProperty("commit") as String?) ?: "dev"

android {
    namespace = "com.couchtv.remote"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.couchtv.remote"
        minSdk = 26          // every Xiaomi phone with an IR blaster runs Android 8 or newer
        targetSdk = 35
        versionCode = buildNumber
        versionName = "1.$buildNumber"
        buildConfigField("String", "COMMIT", "\"$commit\"")
        buildConfigField("String", "UPDATE_URL", "\"https://github.com/charan-mudiraj/CouchTV/releases/download/phone-remote/version.json\"")
    }

    buildFeatures {
        buildConfig = true
    }

    // Updates only install over an app signed with the same key, so published builds use one permanent key
    // (from the REMOTE_KEYSTORE_* repo secrets, see phone-remote/README.md). Local builds use Android's debug key.
    signingConfigs {
        getByName("debug") {
            val keystore = System.getenv("REMOTE_KEYSTORE_FILE")
            if (keystore != null) {
                storeFile = file(keystore)
                storePassword = System.getenv("REMOTE_KEYSTORE_PASSWORD")
                keyAlias = "couchtv"
                keyPassword = System.getenv("REMOTE_KEYSTORE_PASSWORD")
                storeType = "pkcs12"
            }
        }
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
