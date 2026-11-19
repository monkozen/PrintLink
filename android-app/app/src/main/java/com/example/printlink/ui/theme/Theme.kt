
package com.example.printlink.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

private val LightColorScheme = lightColorScheme(
    primary = PrintLinkBlue,
    onPrimary = Color.White,
    primaryContainer = Color(0xFFDCE8FF),
    onPrimaryContainer = PrintLinkDeepBlue,

    secondary = PrintLinkDeepBlue,
    onSecondary = Color.White,
    tertiary = Color(0xFF007A91),

    background = PrintLinkBackground,
    onBackground = PrintLinkInk,

    surface = Color.White,
    onSurface = PrintLinkInk,
    surfaceVariant = Color(0xFFEAF1FF),
    onSurfaceVariant = Color(0xFF50627C),

    surfaceContainerLow = Color.White,
    surfaceContainer = Color(0xFFF3F7FF),
    surfaceContainerHigh = Color(0xFFECF3FF),

    outline = Color(0xFFAFBFD7),
    error = Color(0xFFB3261E)
)

private val DarkColorScheme = darkColorScheme(
    primary = Color(0xFF91B8FF),
    onPrimary = Color(0xFF002A63),

    secondary = PrintLinkCyan,
    onSecondary = Color(0xFF003641),

    background = Color(0xFF0B1F37),
    onBackground = Color.White,

    surface = Color(0xFF152846),
    onSurface = Color.White,
    surfaceVariant = Color(0xFF243C5A),
    onSurfaceVariant = Color(0xFFD6E2F5),

    surfaceContainerLow = Color(0xFF152846),
    surfaceContainer = Color(0xFF1C3150),
    surfaceContainerHigh = Color(0xFF263D5C),

    error = Color(0xFFFFB4AB)
)

@Composable
fun PrintLinkTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    content: @Composable () -> Unit
) {
    MaterialTheme(
        colorScheme = if (darkTheme) {
            DarkColorScheme
        } else {
            LightColorScheme
        },
        typography = Typography,
        content = content
    )
}
