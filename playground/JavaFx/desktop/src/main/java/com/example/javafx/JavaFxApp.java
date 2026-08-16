// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

package com.example.javafx;

import javafx.animation.PauseTransition;
import javafx.application.Application;
import javafx.scene.Scene;
import javafx.scene.control.Label;
import javafx.stage.Stage;
import javafx.util.Duration;

public final class JavaFxApp extends Application {
    @Override
    public void start(Stage stage) {
        String message = createGreeting();
        System.out.println("JAVAFX_SAMPLE_STARTED " + message);

        stage.setTitle("Aspire JavaFX");
        stage.setScene(new Scene(new Label(message), 320, 120));
        stage.show();

        PauseTransition exitDelay = new PauseTransition(Duration.seconds(60));
        exitDelay.setOnFinished(ignored -> {
            System.out.println("JAVAFX_SAMPLE_EXITING");
            stage.close();
        });
        exitDelay.play();
    }

    static String createGreeting() {
        return "JavaFX is running under Aspire";
    }
}
