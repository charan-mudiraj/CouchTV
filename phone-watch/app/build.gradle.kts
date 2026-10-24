plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// GitHub Actions passes -PversionCode=<build number> -Pcommit=<sha>. Local builds are version 1, so any published
// build counts as newer and the app offers it.
val buildNumber = (findProperty("versionCode") as String?)?.toIntOrNull() ?: 1
val commit = (findProperty("commit") as String?) ?: "dev"

android {
    namespace = "com.couchtv.watch"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.couchtv.watch"
        minSdk = 26
        targetSdk = 35
        versionCode = buildNumber
        versionName = "1.$buildNumber"
        buildConfigField("String", "COMMIT", "\"$commit\"")
        buildConfigField("String", "UPDATE_URL", "\"https://github.com/charan-mudiraj/CouchTV/releases/download/phone-watch/version.json\"")
    }

    buildFeatures {
        buildConfig = true
    }

    // The phone remote's permanent key (the REMOTE_KEYSTORE_* repo secrets): updates install over the app only when
    // signed the same, and Play on TV is only for apps signed like the remote. Local builds use Android's debug key.
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

// No libraries, as the remote: only the Android framework (its WebView plays the trailers). JUnit is only for the
// unit test (YouTubeTest), which runs on the build machine, never in the app.
dependencies {
    testImplementation("junit:junit:4.13.2")
}
