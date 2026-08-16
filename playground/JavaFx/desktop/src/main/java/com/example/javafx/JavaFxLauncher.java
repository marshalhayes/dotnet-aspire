// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

package com.example.javafx;

import javafx.application.Application;

public final class JavaFxLauncher {
    private JavaFxLauncher() {
    }

    public static void main(String[] args) {
        Application.launch(JavaFxApp.class, args);
    }
}
